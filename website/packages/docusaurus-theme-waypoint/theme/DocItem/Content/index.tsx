import React from 'react';
import Content from '@theme-init/DocItem/Content';
import Waypoint from '../../Waypoint';

type ContentProps = React.ComponentProps<typeof Content>;

/** Wraps every doc's content so guides that opt in can be read one step at a time. Docs that don't opt in pass
 * straight through. */
export default function ContentWrapper(props: ContentProps): React.ReactElement {
  return (
    <Waypoint>
      <Content {...props} />
    </Waypoint>
  );
}
