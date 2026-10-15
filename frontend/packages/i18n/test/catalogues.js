/** Read actual object keys, including quoted and inline catalogue entries. */
export function catalogueEntries(object, prefix = '', output = {}) {
  for (const [key, value] of Object.entries(object)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (typeof value === 'string') output[path] = value;
    else catalogueEntries(value, path, output);
  }
  return output;
}
