using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Projects the existing complete-carrier codec's exact topology and XYZ lanes.</summary>
public sealed class SkyrimFaceBakeCarrierGeometryReader : ISkyrimFaceBakeCarrierGeometryReader
{
    public SkyrimFaceBakeCarrierGeometryResult Read(ImmutableArray<byte> bytes, Sha256Hash expectedSha256)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            if (bytes.IsDefaultOrEmpty || bytes.Length > 64 * 1024 * 1024 ||
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes.AsSpan()))) != expectedSha256)
                throw new InvalidDataException("Carrier bytes must be bounded and match their exact SHA-256.");
            var document = SseFaceGeomCarrierCodec.Parse(bytes.ToArray());
            var structure = SseFaceGeomCarrierCodec.BuildStructure(document);
            // This existing complete-carrier envelope admits both product-assembled
            // and exported skin graphs without inferring selected-model placement.
            SseFaceGeomCarrierCodec.Qualify(document, structure,
                QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete, diagnostics);
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                return new([], diagnostics.ToImmutable());
            var shapes = document.Blocks.Where(block => block.Type == "BSDynamicTriShape")
                .Select(shape =>
                {
                    var layout = RaceMenuCharGenFaceGeomMergeService.RequireDynamicLayout(shape);
                    var positions = RaceMenuCharGenFaceGeomMergeService.ExtractPositions(document.Data, layout);
                    return new SkyrimFaceBakeCarrierShape(shape.Name!, layout.VertexCount,
                        new Sha256Hash(Convert.ToHexString(SHA256.HashData(positions))),
                        SseFaceGeomCarrierCodec.ComputeDynamicShapeTopologyHash(document, shape));
                }).ToImmutableArray();
            return new(shapes, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        {
            diagnostics.Add(new("face-bake-carrier-geometry-refused", DiagnosticSeverity.Error, exception.Message));
            return new([], diagnostics.ToImmutable());
        }
    }
}
