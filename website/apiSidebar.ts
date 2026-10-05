import type {SidebarsConfig} from '@docusaurus/plugin-content-docs';
import apiItems from './api/sidebar';

// The API reference's sidebar. docusaurus-plugin-openapi-docs writes api/sidebar.ts as a bare list of items, in the
// spec's order and with each method's badge; a sidebars file has to name its sidebars, so this wraps that list.
// api/ is generated from data/openapi/dotmarc-api.json by `docusaurus gen-api-docs all`, which the build, start and
// typecheck scripts run first.
const sidebars: SidebarsConfig = {
  apiSidebar: apiItems,
};

export default sidebars;
