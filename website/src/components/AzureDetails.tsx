import React, {useEffect, useState} from 'react';
import CodeBlock from '@theme/CodeBlock';
import {useLatestVersion} from './LatestVersion';
import styles from './AzureDetails.module.css';

const storageKey = 'dotmarc-docs-azure-details';

interface SavedDetails {
  subscription: string;
  resourceGroup: string;
  previousVersion: string;
}

const empty: SavedDetails = {subscription: '', resourceGroup: '', previousVersion: ''};

/** Escapes a value for a single-quoted PowerShell string. */
function quote(value: string): string {
  return `'${value.replaceAll("'", "''")}'`;
}

export function buildAzureVariables(details: SavedDetails & {version: string}): string {
  const lines: string[] = [];
  if (details.subscription.trim()) {
    lines.push(`az account set --subscription ${quote(details.subscription.trim())}`);
  }
  lines.push(`$RG = ${quote(details.resourceGroup.trim() || '<your-resource-group>')}`);
  lines.push(
    '$APP = az containerapp list --resource-group $RG --query "[0].name" -o tsv',
    'if (-not $APP) { throw "No Container App found in resource group \'$RG\'. Check the subscription and resource group." }',
    `$VERSION = ${quote(details.version.trim() || '<release-version>')}`);
  if (details.previousVersion.trim()) {
    lines.push(`$PREVIOUS_VERSION = ${quote(details.previousVersion.trim())}`);
  }
  return lines.join('\n');
}

/** A small form that turns the reader's own subscription, resource group and version into the variable block the
 * Azure commands below it rely on, so the docs never need an assumed resource group or deployment name. The values
 * stay in this browser's localStorage and are never sent anywhere. The Container App's name is looked up from the
 * resource group instead of being typed in. */
export default function AzureDetails(): React.ReactElement {
  const latest = useLatestVersion();
  const [details, setDetails] = useState<SavedDetails>(empty);
  const [version, setVersion] = useState(latest ?? '');

  useEffect(() => {
    try {
      const raw = window.localStorage.getItem(storageKey);
      if (raw) {
        const saved = JSON.parse(raw);
        setDetails({
          subscription: typeof saved.subscription === 'string' ? saved.subscription : '',
          resourceGroup: typeof saved.resourceGroup === 'string' ? saved.resourceGroup : '',
          previousVersion: typeof saved.previousVersion === 'string' ? saved.previousVersion : '',
        });
      }
    } catch {
      // localStorage unavailable (private browsing, storage blocked): the form still works, it just isn't remembered.
    }
  }, []);

  const update = (key: keyof SavedDetails, value: string) => {
    const next = {...details, [key]: value};
    setDetails(next);
    try {
      window.localStorage.setItem(storageKey, JSON.stringify(next));
    } catch {
      // Nothing to fall back to: the value still applies to this page view.
    }
  };

  return (
    <div className={styles.panel}>
      <p className={styles.title}>Your details</p>
      <div className={styles.fields}>
        <label className={styles.field}>
          <span>Resource group</span>
          <input
            type="text"
            value={details.resourceGroup}
            placeholder="the group dotMARC is deployed in"
            onChange={(event) => update('resourceGroup', event.target.value)}
          />
        </label>
        <label className={styles.field}>
          <span>Subscription ID or name (optional)</span>
          <input
            type="text"
            value={details.subscription}
            placeholder="leave blank to use the current one"
            onChange={(event) => update('subscription', event.target.value)}
          />
        </label>
        <label className={styles.field}>
          <span>Version to deploy</span>
          <input
            type="text"
            value={version}
            placeholder="for example 1.2.3"
            onChange={(event) => setVersion(event.target.value)}
          />
        </label>
        <label className={styles.field}>
          <span>Version to roll back to (optional)</span>
          <input
            type="text"
            value={details.previousVersion}
            placeholder="the release you were on"
            onChange={(event) => update('previousVersion', event.target.value)}
          />
        </label>
      </div>
      <p className={styles.hint}>
        Run this first in PowerShell. Every command below uses these variables. The Container App is found
        from the resource group, so you don't need its name or the deployment name.
      </p>
      <CodeBlock language="powershell">{buildAzureVariables({...details, version})}</CodeBlock>
    </div>
  );
}
