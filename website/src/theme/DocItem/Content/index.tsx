import React from 'react';
import Content from '@theme-original/DocItem/Content';
import StepMode from '@site/src/components/StepMode';

type ContentProps = React.ComponentProps<typeof Content>;

/** Wraps every doc's content so guides that opt in with `stepMode: true` can be read one step at a time. Docs that
 * don't opt in pass straight through. */
export default function ContentWrapper(props: ContentProps): React.ReactElement {
  return (
    <StepMode>
      <Content {...props} />
    </StepMode>
  );
}
