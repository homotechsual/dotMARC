import MDXComponents from '@theme-original/MDXComponents';
import LatestVersion from '@site/src/components/LatestVersion';

// Registers <LatestVersion /> for every MDX doc and post, so it can be used without an import.
export default {
  ...MDXComponents,
  LatestVersion,
};
