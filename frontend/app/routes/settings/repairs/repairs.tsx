import { Alert, Form } from "react-bootstrap";
import styles from "./repairs.module.css"
import { type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";

const backoffTiersKey = "repair.healthcheck.backoff-tiers";

type BackoffTier = { MaxAgeDays: number | null, IntervalDays: number };

type RepairsSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function RepairsSettings({ config, setNewConfig }: RepairsSettingsProps) {
    const libraryDirConfig = config["media.library-dir"];
    const arrConfig = JSON.parse(config["arr.instances"]);
    const areArrInstancesConfigured =
        arrConfig.RadarrInstances.length > 0 ||
        arrConfig.SonarrInstances.length > 0;
    const canEnableRepairs = !!libraryDirConfig && areArrInstancesConfigured;
    var helpText = canEnableRepairs
        ? "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed. If an unhealthy item is part of your Radarr/Sonarr library, a new search will be triggered to find a replacement."
        : "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed and replaced. This setting can only be enabled once your Library-Directory and Radarr/Sonarr instances are configured.";

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="enable-repairs-checkbox"
                    aria-describedby="enable-repairs-help"
                    label={`Enable Background Repairs`}
                    checked={canEnableRepairs && config["repair.enable"] === "true"}
                    disabled={!canEnableRepairs}
                    onChange={e => setNewConfig({ ...config, "repair.enable": "" + e.target.checked })} />
                <Form.Text id="enable-repairs-help" muted>
                    {helpText}
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="library-dir-input">Library Directory</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="library-dir-input"
                    aria-describedby="library-dir-help"
                    value={config["media.library-dir"]}
                    onChange={e => setNewConfig({ ...config, "media.library-dir": e.target.value })} />
                <Form.Text id="library-dir-help" muted>
                    The path to your organized media library that contains all your imported symlinks or *.strm files.
                    Make sure this path is visible to your NzbDAV container.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label>Health Check Schedule</Form.Label>
                {getBackoffTiers(config).map((tier, i) => (
                    <div key={i} className={styles.tierRow}>
                        {tier.MaxAgeDays === null ? (
                            <span className={styles.tierLabel}>Older releases:</span>
                        ) : (
                            <>
                                <span className={styles.tierLabel}>Newer than</span>
                                <Form.Control
                                    type="number"
                                    min={1}
                                    className={styles.tierInput}
                                    value={tier.MaxAgeDays}
                                    onChange={e => setTierField(config, setNewConfig, i, "MaxAgeDays", e.target.value)} />
                                <span className={styles.tierLabel}>days:</span>
                            </>
                        )}
                        <span className={styles.tierLabel}>check every</span>
                        <Form.Control
                            type="number"
                            min={1}
                            className={styles.tierInput}
                            value={tier.IntervalDays}
                            onChange={e => setTierField(config, setNewConfig, i, "IntervalDays", e.target.value)} />
                        <span className={styles.tierLabel}>day(s)</span>
                    </div>
                ))}
                <Form.Text muted>
                    How often each usenet file is re-verified, based on the release's age.
                    Older releases are checked less often to save connections.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

function getBackoffTiers(config: Record<string, string>): BackoffTier[] {
    try {
        const parsed = JSON.parse(config[backoffTiersKey] || "[]");
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        return [];
    }
}

function setTierField(
    config: Record<string, string>,
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>,
    index: number,
    field: keyof BackoffTier,
    value: string,
) {
    const tiers = getBackoffTiers(config);
    const parsed = parseInt(value);
    tiers[index] = { ...tiers[index], [field]: Number.isNaN(parsed) ? 1 : Math.max(1, parsed) };
    setNewConfig({ ...config, [backoffTiersKey]: JSON.stringify(tiers) });
}

export function isRepairsSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["repair.enable"] !== newConfig["repair.enable"]
        || config["media.library-dir"] !== newConfig["media.library-dir"]
        || config[backoffTiersKey] !== newConfig[backoffTiersKey];
}
