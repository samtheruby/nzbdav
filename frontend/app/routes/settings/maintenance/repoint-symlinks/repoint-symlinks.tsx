import { Alert, Button, Form } from "react-bootstrap";
import styles from "./repoint-symlinks.module.css";
import { useCallback, useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";

const taskTopic = { 'rpsl': 'state' };

type RepointSymlinksProps = {
    savedConfig: Record<string, string>
};

export function RepointSymlinks({ savedConfig }: RepointSymlinksProps) {
    // stateful variables
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);
    const [oldMountDir, setOldMountDir] = useState<string>("");

    // derived variables
    const libraryDir = savedConfig["media.library-dir"];
    const newMountDir = savedConfig["rclone.mount-dir"]?.trim() || "/mnt/nzbdav (default)";
    const isFinished = progress?.startsWith("Done") || progress?.startsWith("Failed");
    const isRunning = !isFinished && (isFetching || progress !== null);
    const isRunButtonEnabled = !!libraryDir && !!oldMountDir.trim() && connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? 'success' : 'secondary';
    const runButtonLabel = isRunning ? "⌛ Running.." : '▶ Run Task';

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        let backoff = 1000;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => setProgress(message));
            ws.onopen = () => { backoff = 1000; setConnected(true); ws.send(JSON.stringify(taskTopic)); }
            ws.onclose = () => {
                setConnected(false);
                setProgress(null);
                if (!disposed) {
                    setTimeout(connect, backoff);
                    backoff = Math.min(backoff * 2, 30000);
                }
            };
            ws.onerror = () => { ws.close() };
            return () => { disposed = true; ws.close(); }
        }
        return connect();
    }, [setProgress, setConnected]);

    // events
    const onRun = useCallback(async () => {
        setIsFetching(true);
        await fetch("/api/repoint-symlinks?oldMountDir=" + encodeURIComponent(oldMountDir.trim()));
        setIsFetching(false);
    }, [setIsFetching, oldMountDir]);

    return (
        <>
            {!libraryDir &&
                <Alert className={styles.alert} variant="warning">
                    Warning
                    <ul className={styles.list}>
                        <li className={styles["list-item"]}>
                            You must first configure the Library Directory setting before running this task.
                            Head over to the Repairs tab.
                        </li>
                    </ul>
                </Alert>
            }
            {libraryDir &&
                <Alert className={styles.alert} variant="danger">
                    <span style={{ fontWeight: 'bold' }}>Danger</span>
                    <ul className={styles.list}>
                        <li className={styles["list-item"]}>
                            Make a backup of your entire Library Dir prior to running this task.
                        </li>
                        <li className={styles["list-item"]}>
                            Symlinks in `{libraryDir}` will be rewritten in place and are not recoverable without a backup.
                        </li>
                    </ul>
                </Alert>
            }
            <div className={styles.task}>
                <Form.Group>
                    <Form.Label htmlFor="repoint-old-mount-input">Old (External) Mount Path</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        id="repoint-old-mount-input"
                        placeholder="/mnt/remote/nzbdav"
                        value={oldMountDir}
                        onChange={e => setOldMountDir(e.target.value)} />
                    <Form.Text muted>
                        The path your external rclone sidecar mounted at. Library symlinks pointing under
                        this path will be repointed to the current mount directory (<code>{newMountDir}</code>).
                    </Form.Text>
                    <div className={styles.run}>
                        <Button
                            className={styles["run-button"]}
                            variant={runButtonVariant}
                            onClick={onRun}
                            disabled={!isRunButtonEnabled}
                        >
                            {runButtonLabel}
                        </Button>
                        <div className={styles["task-progress"]}>
                            {progress}
                        </div>
                    </div>
                    <Form.Text id="repoint-task-progress-help" muted>
                        <br />
                        This task scans your organized media library for symlinks that point into the old
                        mount path and rewrites them to the same content under the current mount directory.
                        Run it after switching from an external rclone sidecar to the embedded mount, then the
                        old rclone can be deleted. It is safe to re-run and does nothing on a fresh install.
                    </Form.Text>
                </Form.Group>
            </div>
        </>
    );
}
