import type {ReactNode} from 'react';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';

/** The latest released dotMARC version (for example 0.7.1), as worked out when the site was built (see
 * lib/latest-version.cjs). Usable in any doc as <LatestVersion />, for instance in an image tag:
 * `ghcr.io/homotechsual/dotmarc:<LatestVersion />`. When the version couldn't be determined it renders "latest",
 * which is also a valid image tag, so the sentence stays correct. */
export function useLatestVersion(): string | null {
  const {siteConfig} = useDocusaurusContext();
  const version = siteConfig.customFields?.latestVersion;
  return typeof version === 'string' && version ? version : null;
}

export default function LatestVersion(): ReactNode {
  return <>{useLatestVersion() ?? 'latest'}</>;
}
