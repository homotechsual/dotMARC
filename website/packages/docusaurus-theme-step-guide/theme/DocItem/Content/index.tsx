import React from 'react';
import Content from '@theme-init/DocItem/Content';
import StepGuide from '../../StepGuide';

type ContentProps = React.ComponentProps<typeof Content>;

/** Wraps every doc's content so guides that opt in can be read one step at a time. Docs that don't opt in pass
 * straight through. */
export default function ContentWrapper(props: ContentProps): React.ReactElement {
  return (
    <StepGuide>
      <Content {...props} />
    </StepGuide>
  );
}
