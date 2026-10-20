/** Compile-time flag: production builds never load the demo engine. */
declare const __COUNTFLOW_STATIC_DEMO__: boolean;
export const isStaticDemo = typeof __COUNTFLOW_STATIC_DEMO__ !== 'undefined' && __COUNTFLOW_STATIC_DEMO__;
export function disabledInDemo(label: string) {
  return isStaticDemo ? { disabled: true, title: label } : {};
}
