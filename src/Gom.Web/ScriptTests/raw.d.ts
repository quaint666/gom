/** A file's text, as Vite reads it for a `?raw` import (the service worker is tested from its own source). */
declare module '*?raw' {
  const text: string;
  export default text;
}
