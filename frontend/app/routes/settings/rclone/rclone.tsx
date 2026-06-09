import { Badge, Button, Form, InputGroup, Spinner } from "react-bootstrap";
import styles from "./rclone.module.css"
import { type Dispatch, type SetStateAction, type ReactNode, useState, useCallback, useEffect } from "react";

type RcloneSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

// Keys that the embedded mount reads. Kept in sync with isRcloneSettingsUpdated.
const embeddedMountKeys = [
    "rclone.embedded-mount-enabled",
    "rclone.mount-dir",
    "rclone.vfs-cache-mode",
    "rclone.vfs-cache-max-size",
    "rclone.vfs-cache-max-age",
    "rclone.buffer-size",
    "rclone.vfs-read-ahead",
    "rclone.dir-cache-time",
    "rclone.log-level",
    "rclone.extra-flags",
];

const rcServerKeys = ["rclone.rc-enabled", "rclone.host", "rclone.user", "rclone.pass"];

export function RcloneSettings({ config, setNewConfig }: RcloneSettingsProps) {
    const [connectionState, setConnectionState] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');

    useEffect(() => {
        setConnectionState('idle');
    }, [config["rclone.host"], config["rclone.user"], config["rclone.pass"]]);

    const testConnection = useCallback(async () => {
        const host = config["rclone.host"];
        if (!host?.trim()) {
            return;
        }

        setConnectionState('testing');

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('user', config["rclone.user"] ?? '');
            formData.append('pass', config["rclone.pass"] ?? '');

            const response = await fetch('/api/test-rclone-connection', {
                method: 'POST',
                body: formData
            });

            const result = await response.json();

            if (result.status && result.connected) {
                setConnectionState('success');
            } else {
                setConnectionState('error');
            }
        } catch (error) {
            setConnectionState('error');
        }
    }, [config]);

    const embeddedEnabled = config["rclone.embedded-mount-enabled"] === "true";
    const set = useCallback(
        (key: string, value: string) => setNewConfig({ ...config, [key]: value }),
        [config, setNewConfig]);

    return (
        <div className={styles.container}>
            {/* ---------- Embedded Mount ---------- */}
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="rclone-embedded-mount-checkbox"
                    aria-describedby="rclone-embedded-mount-help"
                    label={`Enable Embedded Rclone Mount`}
                    checked={embeddedEnabled}
                    onChange={e => set("rclone.embedded-mount-enabled", "" + e.target.checked)} />
                <Form.Text id="rclone-embedded-mount-help" muted>
                    Run rclone inside this container instead of a separate sidecar. Requires the
                    container to be started with FUSE privileges
                    (<code>--cap-add SYS_ADMIN --device /dev/fuse --security-opt apparmor:unconfined</code>),
                    a shared mount (e.g. <code>/mnt:/mnt:rshared</code>), and the
                    <code> WEBDAV_PASSWORD</code> env var set to your plaintext WebDAV password so
                    the mount can authenticate. While enabled, that env var is the single source
                    for the WebDAV password — the password field on the WebDAV tab is disabled.
                </Form.Text>
            </Form.Group>

            {embeddedEnabled && (
                <>
                    <hr />
                    <div className={styles.input}>
                        <MountStatusBadge />
                    </div>
                    <TextSetting
                        id="rclone-mount-dir"
                        label="Mount Directory"
                        value={config["rclone.mount-dir"]}
                        placeholder="/mnt/nzbdav"
                        onChange={v => set("rclone.mount-dir", v)}
                        help="Where the WebDAV is mounted on the filesystem. Shared with the SABnzbd 'Rclone Mount Directory' setting." />
                    <SelectSetting
                        id="rclone-vfs-cache-mode"
                        label="VFS Cache Mode"
                        value={config["rclone.vfs-cache-mode"]}
                        options={["off", "minimal", "writes", "full"]}
                        onChange={v => set("rclone.vfs-cache-mode", v)}
                        help="'full' is required for seeking and proper streaming." />
                    <TextSetting
                        id="rclone-vfs-cache-max-size"
                        label="VFS Cache Max Size"
                        value={config["rclone.vfs-cache-max-size"]}
                        placeholder="20G"
                        onChange={v => set("rclone.vfs-cache-max-size", v)}
                        help="Max local disk used by the cache. Adjust to your storage." />
                    <TextSetting
                        id="rclone-vfs-cache-max-age"
                        label="VFS Cache Max Age"
                        value={config["rclone.vfs-cache-max-age"]}
                        placeholder="24h"
                        onChange={v => set("rclone.vfs-cache-max-age", v)}
                        help="How long cached data is kept." />
                    <TextSetting
                        id="rclone-buffer-size"
                        label="Buffer Size"
                        value={config["rclone.buffer-size"]}
                        placeholder="0M"
                        onChange={v => set("rclone.buffer-size", v)}
                        help="In-memory buffer per open file. '0M' avoids double-caching (RAM + disk)." />
                    <TextSetting
                        id="rclone-vfs-read-ahead"
                        label="VFS Read Ahead"
                        value={config["rclone.vfs-read-ahead"]}
                        placeholder="512M"
                        onChange={v => set("rclone.vfs-read-ahead", v)}
                        help="Bytes read ahead into the cache for smooth playback." />
                    <TextSetting
                        id="rclone-dir-cache-time"
                        label="Dir Cache Time"
                        value={config["rclone.dir-cache-time"]}
                        placeholder="20s"
                        onChange={v => set("rclone.dir-cache-time", v)}
                        help="How long directory listings are cached. nzbdav refreshes this automatically when files change." />
                    <SelectSetting
                        id="rclone-log-level"
                        label="Log Level"
                        value={config["rclone.log-level"]}
                        options={["DEBUG", "INFO", "NOTICE", "ERROR"]}
                        onChange={v => set("rclone.log-level", v)}
                        help="Verbosity of rclone's logs in the container output." />
                    <TextSetting
                        id="rclone-extra-flags"
                        label="Additional Flags (advanced)"
                        value={config["rclone.extra-flags"]}
                        placeholder="--vfs-read-chunk-size=128M"
                        onChange={v => set("rclone.extra-flags", v)}
                        help="Extra rclone flags, space-separated. Appended last so they override the above. --links, --use-cookies and --allow-other are always applied." />
                </>
            )}

            <hr />

            {/* ---------- External RC Server ---------- */}
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="rclone-rc-enabled-checkbox"
                    aria-describedby="rclone-rc-enabled-help"
                    label={`Enable Rclone RC Server Notifications`}
                    checked={config["rclone.rc-enabled"] === "true"}
                    disabled={embeddedEnabled}
                    onChange={e => set("rclone.rc-enabled", "" + e.target.checked)} />
                <Form.Text id="rclone-rc-enabled-help" muted>
                    {embeddedEnabled
                        ? "Managed automatically while the embedded mount is enabled — nzbdav notifies the embedded rclone directly."
                        : "When enabled, nzbdav will automatically notify your rclone mount via the RC API whenever files are added or removed on the webdav. This allows setting a high dir-cache-time setting on Rclone."}
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-host-input">Rclone Server Host</Form.Label>
                <InputGroup className={styles.input}>
                    <Form.Control
                        type="text"
                        id="rclone-host-input"
                        aria-describedby="rclone-host-help"
                        placeholder="http://localhost:5572"
                        disabled={embeddedEnabled}
                        value={config["rclone.host"]}
                        onChange={e => set("rclone.host", e.target.value)} />
                    {!embeddedEnabled && config["rclone.host"]?.trim() && (
                        <Button
                            variant={connectionState === 'success' ? 'success' :
                                connectionState === 'error' ? 'danger' : 'secondary'}
                            onClick={testConnection}
                            disabled={connectionState === 'testing'}
                            className={styles.testButton}
                        >
                            {
                                connectionState === 'testing' ? (
                                    <Spinner animation="border" size="sm" />
                                ) : connectionState === 'success' ? (
                                    '✓'
                                ) : connectionState === 'error' ? (
                                    '✗'
                                ) : (
                                    'Test Conn'
                                )
                            }
                        </Button>
                    )}
                </InputGroup>
                <Form.Text id="rclone-host-help" muted>
                    The host address of the rclone RC API.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-user-input">Rclone Server User</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="rclone-user-input"
                    aria-describedby="rclone-user-help"
                    disabled={embeddedEnabled}
                    value={config["rclone.user"]}
                    onChange={e => set("rclone.user", e.target.value)} />
                <Form.Text id="rclone-user-help" muted>
                    The username for authenticating to the rclone RC API. This field is optional.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="rclone-pass-input">Rclone Server Password</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="password"
                    id="rclone-pass-input"
                    aria-describedby="rclone-pass-help"
                    disabled={embeddedEnabled}
                    value={config["rclone.pass"]}
                    onChange={e => set("rclone.pass", e.target.value)} />
                <Form.Text id="rclone-pass-help" muted>
                    The password for authenticating to the rclone RC API. This field is optional.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

type TextSettingProps = {
    id: string;
    label: string;
    value: string;
    placeholder?: string;
    help: ReactNode;
    onChange: (value: string) => void;
};

function TextSetting({ id, label, value, placeholder, help, onChange }: TextSettingProps) {
    return (
        <Form.Group>
            <Form.Label htmlFor={`${id}-input`}>{label}</Form.Label>
            <Form.Control
                className={styles.input}
                type="text"
                id={`${id}-input`}
                aria-describedby={`${id}-help`}
                placeholder={placeholder}
                value={value}
                onChange={e => onChange(e.target.value)} />
            <Form.Text id={`${id}-help`} muted>{help}</Form.Text>
        </Form.Group>
    );
}

type SelectSettingProps = {
    id: string;
    label: string;
    value: string;
    options: string[];
    help: ReactNode;
    onChange: (value: string) => void;
};

function SelectSetting({ id, label, value, options, help, onChange }: SelectSettingProps) {
    return (
        <Form.Group>
            <Form.Label htmlFor={`${id}-input`}>{label}</Form.Label>
            <Form.Select
                className={styles.input}
                id={`${id}-input`}
                aria-describedby={`${id}-help`}
                value={value}
                onChange={e => onChange(e.target.value)}>
                {options.map(o => <option key={o} value={o}>{o}</option>)}
            </Form.Select>
            <Form.Text id={`${id}-help`} muted>{help}</Form.Text>
        </Form.Group>
    );
}

type MountStatus = {
    enabled: boolean;
    running: boolean;
    pid?: number | null;
    startedAtUtc?: string | null;
    lastError?: string | null;
    rcloneVersion?: string | null;
    cacheBytesUsed?: number | null;
    cacheMaxSize?: string | null;
};

function MountStatusBadge() {
    const [status, setStatus] = useState<MountStatus | null>(null);
    const [failed, setFailed] = useState(false);

    useEffect(() => {
        let active = true;
        const poll = async () => {
            try {
                const res = await fetch("/api/rclone-mount-status");
                if (!res.ok) throw new Error();
                const data = await res.json();
                if (active) { setStatus(data); setFailed(false); }
            } catch {
                if (active) setFailed(true);
            }
        };
        poll();
        const interval = setInterval(poll, 5000);
        return () => { active = false; clearInterval(interval); };
    }, []);

    if (failed) return <Badge bg="secondary">Status unavailable</Badge>;
    if (!status) return <Badge bg="secondary">Checking…</Badge>;

    if (status.running) {
        const details: string[] = [];
        if (status.rcloneVersion) details.push(status.rcloneVersion);
        if (status.cacheBytesUsed != null) {
            const used = formatBytes(status.cacheBytesUsed);
            details.push(`cache ${used}${status.cacheMaxSize ? ` / ${status.cacheMaxSize}` : ""}`);
        }
        return <Badge bg="success">● Mounted{details.length ? ` (${details.join(", ")})` : ""}</Badge>;
    }
    if (status.lastError) return <Badge bg="danger">● Not running — {status.lastError}</Badge>;
    return <Badge bg="warning" text="dark">● Starting…</Badge>;
}

function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    const units = ["KB", "MB", "GB", "TB"];
    let value = bytes / 1024;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return `${value.toFixed(1)} ${units[unit]}`;
}

export function isRcloneSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return [...embeddedMountKeys, ...rcServerKeys].some(key => config[key] !== newConfig[key]);
}
