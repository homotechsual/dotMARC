// A Docusaurus theme: it overrides the doc page's content component (theme/DocItem/Content) so a guide can opt in to
// being read one step at a time. See README.md for the options.
const path = require('path');

module.exports = function themeStepGuide() {
  return {
    name: '@homotechsual/docusaurus-theme-step-guide',
    // The theme is TypeScript source that the site's own build compiles. Don't also return it from
    // getTypeScriptThemePath: it would then be a second theme layer, and its @theme-original import would resolve
    // to itself.
    getThemePath() {
      return path.join(__dirname, 'theme');
    },
  };
};

module.exports.validateThemeConfig = function validateThemeConfig({validate, themeConfig}) {
  const {Joi} = require('@docusaurus/utils-validation');
  const schema = Joi.object({
    stepGuide: Joi.object({
      // Docs with this front matter key set to true get the switch.
      frontMatterKey: Joi.string().default('stepGuide'),
      // Which heading levels start a new step.
      headingLevels: Joi.array().items(Joi.number().valid(2, 3, 4, 5, 6)).min(1).default([2, 3]),
      // Prefix for the keys the theme writes to the reader's localStorage.
      storageNamespace: Joi.string().default('step-guide'),
      // Show the dismissible "new" callout to readers who haven't used the switch yet.
      showIntroHint: Joi.boolean().default(true),
      // The element(s) whose children are the guide's content: a CSS selector, a comma-separated list, or an array.
      articleSelector: Joi.alternatives(Joi.string(), Joi.array().items(Joi.string()).min(1)).default('.theme-doc-markdown'),
      // Wrapper elements to look inside, so headings in them still start steps.
      wrapperSelectors: Joi.array().items(Joi.string()).default([]),
    }).default({}),
  });
  return validate(schema, themeConfig);
};
