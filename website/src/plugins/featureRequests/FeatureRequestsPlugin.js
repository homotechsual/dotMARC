/**
 * Copyright (c) Facebook, Inc. and its affiliates.
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this source tree.
 */

import {normalizeUrl} from '@docusaurus/utils';

/**
 * @param {import('@docusaurus/types').LoadContext} context
 * @returns {import('@docusaurus/types').Plugin}
 */
export default function FeatureRequestsPlugin(context) {
  return {
    name: 'feature-requests-plugin',
    async contentLoaded({actions}) {
      for (const route of ['/feedback', '/roadmap']) {
        actions.addRoute({
          path: normalizeUrl([context.baseUrl, route]),
          exact: true,
          component: '@site/src/plugins/featureRequests/FeatureRequestsPage',
        });
      }
    },
  };
}
