/**
 * Copyright (c) Facebook, Inc. and its affiliates.
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this source tree.
 */

import React, {type ReactNode, useEffect, useState} from 'react';
import clsx from 'clsx';
import {useColorMode} from '@docusaurus/theme-common';
import Layout from '@theme/Layout';

import cannyScript from './cannyScript';
import styles from './styles.module.css';

const BOARD_TOKEN = '15f43ba5-535f-4bba-bee3-1776018d433b';

// TODO useColorMode() hook is not reliable on first call
//  To ensure we always init Canny with the correct theme, we prefer to delay
//  initialization until we know the theme is correct
//  See also https://github.com/facebook/docusaurus/issues/7986
//  See also https://github.com/facebook/docusaurus/pull/11224
function useIsColorModeReliable() {
  const [isColorModeReliable, setIsColorModeReliable] = useState(false);
  useEffect(() => {
    setIsColorModeReliable(true);
  }, []);
  return isColorModeReliable;
}

function useCannyTheme() {
  const {colorMode} = useColorMode();
  const isColorModeReliable = useIsColorModeReliable();
  if (!isColorModeReliable) {
    return null;
  }
  return colorMode === 'light' ? 'light' : 'dark';
}

function CannyWidget({basePath}: {basePath: string}) {
  useEffect(() => {
    cannyScript();
  }, []);

  const theme = useCannyTheme();
  useEffect(() => {
    if (!theme) {
      return;
    }
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const {Canny} = window as any;
    Canny('render', {
      boardToken: BOARD_TOKEN,
      basePath,
      theme,
    });
  }, [basePath, theme]);

  return (
    <main
      key={theme} // widget needs a full reset: unable to update the theme
      className={clsx('container', 'margin-vert--lg', styles.main)}
      data-canny
    />
  );
}

export default function FeatureRequests({
  basePath,
}: {
  basePath: string;
}): ReactNode {
  return (
    <Layout title="Feedback" description="DotMARC Feedback and Feature Requests page">
      <CannyWidget basePath={basePath} />
    </Layout>
  );
}
