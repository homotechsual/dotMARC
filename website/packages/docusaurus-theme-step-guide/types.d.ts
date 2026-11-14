// Type declarations for the theme's own imports. @theme-init is how a theme wraps another theme's component (a site
// uses @theme-original in its own src/theme folder), and Docusaurus ships no types for it.
declare module '@theme-init/DocItem/Content' {
  import type {ComponentType} from 'react';
  import type {Props} from '@theme/DocItem/Content';

  const Content: ComponentType<Props>;
  export default Content;
}
