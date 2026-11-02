using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>Name of the collection shared by every test that reaches into PDFium.</summary>
public static class PdfiumCollection
{
    public const string Name = "pdfium";
}

/// <summary>
/// Groups the PDFium tests into one collection so xUnit never runs them side by side.
/// </summary>
/// <remarks>
/// PDFium is not thread-safe and fails with a native crash rather than a useful error, which
/// is how a parallel test host took the macOS CI job down. <see cref="PdfDocumentService"/>
/// serializes calls in the product, but a test that forgets to use it would reintroduce the
/// crash; <c>DisableParallelization</c> also keeps the collection from overlapping with any
/// other test in the assembly, so that mistake cannot spread beyond this group.
///
/// This matters because the runners disagree: xUnit executes collections one at a time on a
/// developer machine (measured: two single-test classes 1 s each take 2 s and never overlap),
/// while GitHub Actions interleaves them (the macOS log cuts five classes into thirteen
/// alternating runs). A defect that depends on concurrent PDFium access therefore passes
/// locally and fails only in CI.
/// </remarks>
[CollectionDefinition(PdfiumCollection.Name, DisableParallelization = true)]
public sealed class PdfiumCollectionDefinition
{
}
