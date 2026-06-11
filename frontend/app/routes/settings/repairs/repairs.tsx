import { Alert, Form, InputGroup } from "react-bootstrap";
import styles from "./repairs.module.css"
import { type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";

const backoffTiersKey = "repair.healthcheck.backoff-tiers";
const scheduleEnabledKey = "repair.healthcheck.schedule-enabled";
const scheduleTimeKey = "repair.healthcheck.schedule-time";

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
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="healthcheck-schedule-enabled-checkbox"
                    aria-describedby="healthcheck-schedule-help"
                    label="Schedule Health Checks Daily"
                    checked={isScheduleEnabled(config)}
                    onChange={e => setNewConfig({ ...config, [scheduleEnabledKey]: "" + e.target.checked })} />
                <InputGroup className={styles.input} style={{ marginTop: '15px' }}>
                    <Form.Select
                        disabled={!isScheduleEnabled(config)}
                        value={getScheduledTime(config).hour}
                        onChange={e => setNewConfig({
                            ...config,
                            [scheduleTimeKey]: buildScheduledTime(
                                parseInt(e.target.value),
                                getScheduledTime(config).minute,
                                getScheduledTime(config).period
                            )
                        })}>
                        {Array.from({ length: 12 }, (_, i) => i + 1).map(h => (
                            <option key={h} value={h}>{h}</option>
                        ))}
                    </Form.Select>
                    <Form.Select
                        disabled={!isScheduleEnabled(config)}
                        value={getScheduledTime(config).minute}
                        onChange={e => setNewConfig({
                            ...config,
                            [scheduleTimeKey]: buildScheduledTime(
                                getScheduledTime(config).hour,
                                parseInt(e.target.value),
                                getScheduledTime(config).period
                            )
                        })}>
                        <option value={0}>00</option>
                        <option value={15}>15</option>
                        <option value={30}>30</option>
                        <option value={45}>45</option>
                    </Form.Select>
                    <Form.Select
                        disabled={!isScheduleEnabled(config)}
                        value={getScheduledTime(config).period}
                        onChange={e => setNewConfig({
                            ...config,
                            [scheduleTimeKey]: buildScheduledTime(
                                getScheduledTime(config).hour,
                                getScheduledTime(config).minute,
                                e.target.value as "am" | "pm"
                            )
                        })}>
                        <option value="am">am</option>
                        <option value="pm">pm</option>
                    </Form.Select>
                </InputGroup>
                <Form.Text id="healthcheck-schedule-help" muted>
                    When enabled, health checks only run once per day starting at the specified time,
                    instead of continuously. This is useful for confining checks to off-hours.
                    You may need to set the TZ env variable to ensure the correct timezone.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

function isScheduleEnabled(config: Record<string, string>) {
    return config[scheduleEnabledKey] === "true";
}

function getScheduledTime(config: Record<string, string>): { hour: number, minute: number, period: "am" | "pm" } {
    const totalMinutes = parseInt(config[scheduleTimeKey] || "0");
    return {
        hour: Math.floor(totalMinutes / 60) % 12 || 12,
        minute: totalMinutes % 60,
        period: Math.floor(totalMinutes / 60) >= 12 ? "pm" : "am"
    };
}

function buildScheduledTime(hour: number, minute: number, period: "am" | "pm"): string {
    const hour24 = (hour % 12) + (period === "pm" ? 12 : 0);
    return "" + (hour24 * 60 + minute);
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
        || config[backoffTiersKey] !== newConfig[backoffTiersKey]
        || config[scheduleEnabledKey] !== newConfig[scheduleEnabledKey]
        || config[scheduleTimeKey] !== newConfig[scheduleTimeKey];
}
