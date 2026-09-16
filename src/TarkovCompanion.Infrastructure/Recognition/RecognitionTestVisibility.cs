using System.Runtime.CompilerServices;

// The Tesseract page-reader seam is internal: production composition must never be able to
// substitute scripted OCR. Recognition tests use it to prove the provider gate, deadline and
// limits on hosts where the native library does not load.
[assembly: InternalsVisibleTo("TarkovCompanion.RecognitionTests")]

// The screenshot loader's decode seam, for the one suite that carries Skia's Linux native assets.
[assembly: InternalsVisibleTo("TarkovCompanion.UnitTests")]
