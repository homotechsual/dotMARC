export const CHANGE_TYPES = [
  {id: 'feature', name: 'New feature'},
  {id: 'enhancement', name: 'Enhancement'},
  {id: 'bug', name: 'Bug fix'},
] as const;

export const PRODUCT_AREAS = [
  {id: 'domains', name: 'Dashboard & domain management'},
  {id: 'dmarc', name: 'DMARC & reporting'},
  {id: 'dns', name: 'DNS & provider push'},
  {id: 'spf-dkim', name: 'SPF & DKIM'},
  {id: 'mta-sts', name: 'MTA-STS & TLS reporting'},
  {id: 'senders', name: 'Senders & investigation'},
  {id: 'alerts', name: 'Alerts & notifications'},
  {id: 'psa', name: 'PSA integrations'},
  {id: 'client-reporting', name: 'Client reports & portal'},
  {id: 'access-api', name: 'Access & API'},
  {id: 'operations', name: 'Deployment & operations'},
  {id: 'website', name: 'Website & documentation'},
  {id: 'other', name: 'Other / not sure'},
] as const;

export type ChangeType = typeof CHANGE_TYPES[number]['id'];
export type ProductArea = typeof PRODUCT_AREAS[number]['id'];

export function isChangeType(value: unknown): value is ChangeType {
  return CHANGE_TYPES.some(type => type.id === value);
}

export function isProductArea(value: unknown): value is ProductArea {
  return PRODUCT_AREAS.some(area => area.id === value);
}

export function readClassification(labels: string[]): {changeType: ChangeType | null; areas: ProductArea[]; classificationWarning: boolean} {
  const explicit = CHANGE_TYPES.filter(type => labels.includes(`type:${type.id}`));
  const legacy = CHANGE_TYPES.filter(type => (type.id === 'bug' && labels.includes('bug'))
    || (type.id === 'enhancement' && labels.includes('enhancement')));
  const types = explicit.length ? explicit : legacy;
  const areas = PRODUCT_AREAS.filter(area => labels.includes(`area:${area.id}`)).map(area => area.id);
  return {changeType: types.length === 1 ? types[0].id : null, areas,
    classificationWarning: types.length !== 1 || areas.length === 0};
}
