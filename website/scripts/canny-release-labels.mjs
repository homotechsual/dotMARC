/**
 * Decides how a roadmap idea's release is shown on Canny, kept apart from the sync script so it can be
 * tested without a Canny key. "Released in" and a "complete" status are only true once the version has
 * really been released; before that an idea that is finished in the code still says "Target release".
 *
 * `released` is true when the version has a GitHub release, false when it definitely doesn't, and
 * undefined or null when that couldn't be established (treated as "don't claim it shipped").
 */

export const RELEASE_LINE = /^(Target release|Released in):/;
export const TARGET_LINE = /^Target release:[^\n]*/;
const RELEASED_LINE = /^Released in:[^\n]*/;

/** The label for an idea's release line. */
export function releaseLabel(idea, released) {
  return idea.status === 'complete' && released === true ? 'Released in' : 'Target release';
}

/** The status a new post is created with: complete only once the version has been released. */
export function statusForNewPost(idea, released) {
  return idea.status === 'complete' && released !== true ? 'in progress' : idea.status;
}

/** Whether an existing post should now be moved to complete on Canny. */
export function shouldMarkComplete(idea, existingStatus, released) {
  return idea.status === 'complete' && existingStatus !== 'complete' && released === true;
}

/**
 * Corrects the first line of a post's description for a complete idea, or returns null when it's fine:
 * "Target release: vX" becomes "Released in: vX" once vX is released, and a premature
 * "Released in: vX" goes back to "Target release: vX" when vX definitely isn't released. Only the first
 * line is touched, and only when it names this idea's own version.
 */
export function correctReleaseLine(details, idea, released) {
  if (!idea.version || idea.status !== 'complete') {
    return null;
  }

  if (released === true && TARGET_LINE.test(details)) {
    return details.replace(TARGET_LINE, `Released in: ${idea.version}`);
  }

  if (released === false) {
    const premature = details.match(/^Released in:\s*(\S+)/);
    if (premature && premature[1].toLowerCase() === idea.version.toLowerCase()) {
      return details.replace(RELEASED_LINE, `Target release: ${idea.version}`);
    }
  }

  return null;
}
