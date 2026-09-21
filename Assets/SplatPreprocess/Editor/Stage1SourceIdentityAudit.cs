using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Gsplat;
using Gsplat.Editor;
using UnityEditor;
using UnityEngine;

namespace SplatPreprocess.Editor
{
    /// <summary>CPU-only, immutable-row validation of the loaded Uncompressed/RUB/SH0 reference.</summary>
    public static class Stage1SourceIdentityAudit
    {
        const int RowsPerChunk = 65536;
        const int MaxHeaderBytes = 65536;

        [Serializable]
        sealed class Mismatch
        {
            public long source_row;
            public string field, expected, actual, expected_float32_bits, actual_float32_bits;
        }

        [Serializable]
        sealed class AuditReport
        {
            public int schema_version = 1;
            public string status = "running", created_utc, source_path, source_sha256;
            public long source_bytes, source_data_offset;
            public int source_count, source_record_bytes, asset_count, asset_pruned_count, sh_degree;
            public string asset_path, asset_guid, imported_file_sha256, unity_version;
            public string upstream_revision = "a2bf458d6b16395e6570e9345f9f4408f92684b8";
            public string compression, source_coordinates;
            public float opacity_prune_threshold;
            public string id_rule = "zero_based_vertex_row", mapping_encoding = "little_endian_uint32";
            public string storage_to_source_path, storage_to_source_sha256;
            public long storage_to_source_bytes, checked_rows, checked_scalars, mismatch_rows, mismatch_scalars;
            public bool bitwise_float32_comparison = true, duplicate_position_fixture_passed, shuffled_fixture_rejected;
            public bool source_and_imported_file_hashes_match;
            public double elapsed_seconds;
            public string error;
            public List<Mismatch> first_mismatches = new();
        }

        sealed class Layout
        {
            public int Count, Fields;
            public long DataOffset;
            public int X, Y, Z, R, G, B, Opacity, ScaleX, ScaleY, ScaleZ, Qw, Qx, Qy, Qz;
            public int RecordBytes => checked(Fields * sizeof(float));
        }

        readonly struct Arrays
        {
            public readonly Vector3[] Positions, Scales;
            public readonly Vector4[] Colors, Rotations;

            public Arrays(Vector3[] positions, Vector4[] colors, Vector3[] scales, Vector4[] rotations)
            {
                Positions = positions; Colors = colors; Scales = scales; Rotations = rotations;
            }

            public void ValidateLength(int count)
            {
                if (Positions?.Length != count || Colors?.Length != count || Scales?.Length != count || Rotations?.Length != count)
                    throw new InvalidDataException("Every imported attribute array must have exactly N entries.");
            }
        }

        /// <summary>
        /// Validate all loaded CPU attribute slots against the original source at the same row index.
        /// Writes an explicit identity map only after every row passes. Existing results are preserved.
        /// </summary>
        public static string Run(string sourcePath, string outputDirectory)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("This audit expects a little-endian Unity host.");
            sourcePath = Path.GetFullPath(sourcePath);
            outputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            string reportPath = Path.Combine(outputDirectory, "identity_report.json");
            string mappingPath = Path.Combine(outputDirectory, "storage_to_source.bin");
            if (File.Exists(reportPath) || File.Exists(mappingPath))
                throw new IOException("Use a fresh audit output directory; existing identity results are preserved.");

            string temporaryMap = Path.Combine(outputDirectory, "storage_to_source." + Guid.NewGuid().ToString("N") + ".partial");
            var watch = Stopwatch.StartNew();
            var report = new AuditReport
            {
                created_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                source_path = sourcePath,
                unity_version = Application.unityVersion
            };
            try
            {
                ValidateAuditFixtures();
                report.duplicate_position_fixture_passed = true;
                report.shuffled_fixture_rejected = true;

                // FileShare.Read prevents a concurrent write while hashing and validating this source.
                using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.SequentialScan);
                var layout = ReadHeader(source);
                report.source_count = layout.Count;
                report.source_bytes = source.Length;
                report.source_data_offset = layout.DataOffset;
                report.source_record_bytes = layout.RecordBytes;
                if (source.Length != layout.DataOffset + (long)layout.Count * layout.RecordBytes)
                    throw new InvalidDataException("Source file length does not match its float32 vertex layout.");

                var asset = FindLoadedAsset(sourcePath, layout.Count);
                report.asset_path = AssetDatabase.GetAssetPath(asset);
                report.asset_guid = AssetDatabase.AssetPathToGUID(report.asset_path);
                report.asset_count = checked((int)asset.SplatCount);
                report.asset_pruned_count = checked((int)asset.PrunedSplatCount);
                report.sh_degree = asset.SHBands;
                var importer = AssetImporter.GetAtPath(report.asset_path) as GsplatImporter;
                if (!importer) throw new InvalidDataException("The loaded asset must use GsplatImporter.");
                report.compression = importer.Compression.ToString();
                report.source_coordinates = importer.SourceCoordinates.ToString();
                report.opacity_prune_threshold = importer.OpacityPruneThreshold;
                if (importer.Compression != CompressionMode.Uncompressed || importer.SourceCoordinates != SourceCoordinates.RUB ||
                    importer.OpacityPruneThreshold != 0 || asset.PrunedSplatCount != 0 || asset.SHBands != 0)
                    throw new InvalidDataException("Identity audit requires Uncompressed, explicit RUB, zero pruning, and SH0.");
                if (asset.SplatCount != layout.Count || (asset.SHs != null && asset.SHs.Length != 0))
                    throw new InvalidDataException("The loaded asset count/SH arrays do not match the SH0 source.");
                var arrays = new Arrays(asset.Positions, asset.Colors, asset.Scales, asset.Rotations);
                arrays.ValidateLength(layout.Count);

                source.Position = 0;
                using (var hash = SHA256.Create()) report.source_sha256 = Hex(hash.ComputeHash(source));
                string importedPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", report.asset_path));
                report.imported_file_sha256 = HashFile(importedPath);
                report.source_and_imported_file_hashes_match = report.source_sha256 == report.imported_file_sha256;
                if (!report.source_and_imported_file_hashes_match)
                    throw new InvalidDataException("The imported PLY copy is not byte-identical to the authoritative original.");

                source.Position = layout.DataOffset;
                using (var mapping = new FileStream(temporaryMap, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           1024 * 1024, FileOptions.SequentialScan))
                    CompareRows(source, layout, arrays, mapping, report);

                if (report.mismatch_scalars != 0)
                    throw new InvalidDataException($"{report.mismatch_rows} rows contain {report.mismatch_scalars} mismatched or nonfinite attributes.");
                report.storage_to_source_sha256 = HashFile(temporaryMap);
                report.storage_to_source_bytes = new FileInfo(temporaryMap).Length;
                if (report.storage_to_source_bytes != (long)layout.Count * sizeof(uint))
                    throw new InvalidDataException("Identity sidecar has the wrong length.");
                File.Move(temporaryMap, mappingPath);
                report.storage_to_source_path = mappingPath;
                report.status = "passed";
            }
            catch (Exception error)
            {
                report.status = "failed";
                report.error = error.Message;
            }
            finally
            {
                if (File.Exists(temporaryMap)) File.Delete(temporaryMap);
                report.elapsed_seconds = watch.Elapsed.TotalSeconds;
            }

            string temporaryReport = reportPath + "." + Guid.NewGuid().ToString("N") + ".partial";
            File.WriteAllText(temporaryReport, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            File.Move(temporaryReport, reportPath);
            return report.status == "passed"
                ? $"PASSED: {report.checked_rows:N0} source rows, {report.checked_scalars:N0} exact float32 checks; identity map SHA-256 {report.storage_to_source_sha256}; {reportPath}"
                : $"FAILED after {report.checked_rows:N0} rows: {report.error}; {reportPath}";
        }

        static GsplatAssetUncompressed FindLoadedAsset(string sourcePath, int count)
        {
            // Disabled renderers remain discoverable while the worker holds the GPU preview lease.
            var candidates = UnityEngine.Object.FindObjectsByType<GsplatRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(renderer => renderer.GsplatAsset).OfType<GsplatAssetUncompressed>()
                .Where(asset => asset.SplatCount == count).Distinct().ToArray();
            string filename = Path.GetFileName(sourcePath);
            var sameName = candidates.Where(asset => string.Equals(Path.GetFileName(AssetDatabase.GetAssetPath(asset)), filename,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameName.Length == 1) return sameName[0];
            if (sameName.Length == 0 && candidates.Length == 1) return candidates[0];
            throw new InvalidDataException($"Expected one loaded source renderer with N={count}; found {candidates.Length} candidates ({sameName.Length} with the original filename).");
        }

        static Layout ReadHeader(Stream stream)
        {
            int bytesRead = 0;
            string Line()
            {
                var bytes = new List<byte>();
                while (true)
                {
                    int value = stream.ReadByte();
                    if (value < 0) throw new EndOfStreamException("PLY header is incomplete.");
                    if (++bytesRead > MaxHeaderBytes) throw new InvalidDataException("PLY header exceeds the audit limit.");
                    if (value == '\n') break;
                    if (value != '\r') bytes.Add((byte)value);
                }
                return Encoding.ASCII.GetString(bytes.ToArray()).Trim();
            }

            if (Line() != "ply") throw new InvalidDataException("Expected a PLY source.");
            bool correctFormat = false, foundVertex = false, inVertex = false;
            var properties = new Dictionary<string, int>(StringComparer.Ordinal);
            int count = 0;
            while (true)
            {
                string line = Line();
                if (line == "end_header") break;
                string[] tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0 || tokens[0] == "comment" || tokens[0] == "obj_info") continue;
                if (tokens[0] == "format")
                {
                    if (correctFormat || tokens.Length != 3 || tokens[1] != "binary_little_endian" || tokens[2] != "1.0")
                        throw new InvalidDataException("Only binary_little_endian PLY 1.0 is supported.");
                    correctFormat = true;
                }
                else if (tokens[0] == "element")
                {
                    if (tokens.Length != 3 || !int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out int elementCount))
                        throw new InvalidDataException("Invalid PLY element count.");
                    inVertex = tokens[1] == "vertex";
                    if (inVertex)
                    {
                        if (foundVertex) throw new InvalidDataException("Duplicate vertex elements are unsupported.");
                        foundVertex = true;
                        count = elementCount;
                    }
                    else if (elementCount != 0) throw new InvalidDataException("The audit expects only vertex records.");
                }
                else if (tokens[0] == "property" && inVertex)
                {
                    if (tokens.Length != 3 || (tokens[1] != "float" && tokens[1] != "float32") || properties.ContainsKey(tokens[2]))
                        throw new InvalidDataException("Vertex properties must be unique scalar float32 fields.");
                    if (tokens[2].StartsWith("f_rest_", StringComparison.Ordinal))
                        throw new InvalidDataException("This bounded source audit validates the SH0 reference profile.");
                    properties.Add(tokens[2], properties.Count);
                }
                else if (tokens[0] != "property") throw new InvalidDataException("Unsupported PLY header entry: " + tokens[0]);
            }
            if (!correctFormat || !foundVertex || count <= 0) throw new InvalidDataException("A nonempty float32 vertex source is required.");
            int Field(string name) => properties.TryGetValue(name, out int offset) ? offset :
                throw new InvalidDataException("Missing source property: " + name);
            return new Layout
            {
                Count = count, Fields = properties.Count, DataOffset = stream.Position,
                X = Field("x"), Y = Field("y"), Z = Field("z"),
                R = Field("f_dc_0"), G = Field("f_dc_1"), B = Field("f_dc_2"), Opacity = Field("opacity"),
                ScaleX = Field("scale_0"), ScaleY = Field("scale_1"), ScaleZ = Field("scale_2"),
                Qw = Field("rot_0"), Qx = Field("rot_1"), Qy = Field("rot_2"), Qz = Field("rot_3")
            };
        }

        static void CompareRows(Stream source, Layout layout, Arrays arrays, Stream mapping, AuditReport report)
        {
            arrays.ValidateLength(layout.Count);
            int chunkRows = Math.Min(layout.Count,
                Math.Min(RowsPerChunk, Math.Max(1, (4 * 1024 * 1024) / layout.RecordBytes)));
            var bytes = new byte[checked(chunkRows * layout.RecordBytes)];
            var values = new float[checked(chunkRows * layout.Fields)];
            var ids = mapping == null ? null : new uint[chunkRows];
            var idBytes = mapping == null ? null : new byte[chunkRows * sizeof(uint)];
            for (int firstRow = 0; firstRow < layout.Count;)
            {
                int count = Math.Min(chunkRows, layout.Count - firstRow);
                int byteCount = count * layout.RecordBytes;
                ReadExactly(source, bytes, byteCount);
                Buffer.BlockCopy(bytes, 0, values, 0, byteCount);
                for (int j = 0; j < count; j++)
                {
                    int row = firstRow + j, start = j * layout.Fields;
                    long mismatchesBefore = report.mismatch_scalars;
                    var position = arrays.Positions[row];
                    var color = arrays.Colors[row];
                    var scale = arrays.Scales[row];
                    var rotation = arrays.Rotations[row];
                    // RUB -> Unity RUF: flip Z once and conjugate quaternion imaginary X/Y.
                    var expectedRotation = new Vector4(values[start + layout.Qw], -values[start + layout.Qx],
                        -values[start + layout.Qy], values[start + layout.Qz]).normalized;
                    Compare(report, row, "position.x", values[start + layout.X], position.x);
                    Compare(report, row, "position.y", values[start + layout.Y], position.y);
                    Compare(report, row, "position.z", -values[start + layout.Z], position.z);
                    Compare(report, row, "dc.r", values[start + layout.R], color.x);
                    Compare(report, row, "dc.g", values[start + layout.G], color.y);
                    Compare(report, row, "dc.b", values[start + layout.B], color.z);
                    Compare(report, row, "opacity", 1f / (1f + Mathf.Exp(-values[start + layout.Opacity])), color.w);
                    Compare(report, row, "scale.x", Mathf.Exp(values[start + layout.ScaleX]), scale.x);
                    Compare(report, row, "scale.y", Mathf.Exp(values[start + layout.ScaleY]), scale.y);
                    Compare(report, row, "scale.z", Mathf.Exp(values[start + layout.ScaleZ]), scale.z);
                    Compare(report, row, "rotation.w", expectedRotation.x, rotation.x);
                    Compare(report, row, "rotation.x", expectedRotation.y, rotation.y);
                    Compare(report, row, "rotation.y", expectedRotation.z, rotation.z);
                    Compare(report, row, "rotation.z", expectedRotation.w, rotation.w);
                    report.checked_rows++;
                    if (report.mismatch_scalars != mismatchesBefore) report.mismatch_rows++;
                    if (ids != null) ids[j] = (uint)row;
                }
                if (mapping != null)
                {
                    Buffer.BlockCopy(ids, 0, idBytes, 0, count * sizeof(uint));
                    mapping.Write(idBytes, 0, count * sizeof(uint));
                }
                firstRow += count;
            }
        }

        static void Compare(AuditReport report, int row, string field, float expected, float actual)
        {
            report.checked_scalars++;
            int expectedBits = BitConverter.SingleToInt32Bits(expected), actualBits = BitConverter.SingleToInt32Bits(actual);
            if (expectedBits == actualBits && !float.IsNaN(expected) && !float.IsInfinity(expected)) return;
            report.mismatch_scalars++;
            if (report.first_mismatches.Count >= 8) return;
            report.first_mismatches.Add(new Mismatch
            {
                source_row = row, field = field,
                expected = expected.ToString("R", CultureInfo.InvariantCulture), actual = actual.ToString("R", CultureInfo.InvariantCulture),
                expected_float32_bits = unchecked((uint)expectedBits).ToString("x8"), actual_float32_bits = unchecked((uint)actualBits).ToString("x8")
            });
        }

        static void ReadExactly(Stream stream, byte[] bytes, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(bytes, offset, count - offset);
                if (read == 0) throw new EndOfStreamException("Source ended inside a vertex chunk.");
                offset += read;
            }
        }

        static string HashFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var hash = SHA256.Create();
            return Hex(hash.ComputeHash(stream));
        }

        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        static void ValidateAuditFixtures()
        {
            // These hand-checked rows share a position but have different color and rotation.
            // The production comparison must accept canonical storage and reject a swapped pair.
            string[] fields = { "x", "y", "z", "rot_0", "rot_1", "rot_2", "rot_3", "scale_0", "scale_1", "scale_2", "opacity", "f_dc_0", "f_dc_1", "f_dc_2" };
            string header = "ply\nformat binary_little_endian 1.0\nelement vertex 2\n" +
                            string.Join("\n", fields.Select(field => "property float " + field)) + "\nend_header\n";
            float[] records =
            {
                1, 2, 3, 1, 0, 0, 0, 0, 0, 0, 0, .1f, .2f, .3f,
                1, 2, 3, 0, 1, 0, 0, 0, 0, 0, 0, .7f, .8f, .9f
            };
            using var source = new MemoryStream();
            byte[] headerBytes = Encoding.ASCII.GetBytes(header), bodyBytes = new byte[records.Length * sizeof(float)];
            Buffer.BlockCopy(records, 0, bodyBytes, 0, bodyBytes.Length);
            source.Write(headerBytes, 0, headerBytes.Length);
            source.Write(bodyBytes, 0, bodyBytes.Length);
            source.Position = 0;
            var layout = ReadHeader(source);
            float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            var arrays = new Arrays(new[] { new Vector3(1, 2, -3), new Vector3(1, 2, -3) },
                new[] { new Vector4(.1f, .2f, .3f, .5f), new Vector4(.7f, .8f, .9f, .5f) },
                new[] { Vector3.one, Vector3.one },
                new[] { new Vector4(1, negativeZero, negativeZero, 0), new Vector4(0, -1, negativeZero, 0) });
            var correct = new AuditReport();
            CompareRows(source, layout, arrays, null, correct);
            if (correct.mismatch_scalars != 0 || correct.checked_rows != 2)
                throw new InvalidOperationException("Identity audit self-check rejected canonical duplicate-position rows.");
            (arrays.Colors[0], arrays.Colors[1]) = (arrays.Colors[1], arrays.Colors[0]);
            (arrays.Rotations[0], arrays.Rotations[1]) = (arrays.Rotations[1], arrays.Rotations[0]);
            source.Position = layout.DataOffset;
            var shuffled = new AuditReport();
            CompareRows(source, layout, arrays, null, shuffled);
            if (shuffled.mismatch_rows != 2)
                throw new InvalidOperationException("Identity audit self-check failed to reject shuffled storage rows.");
        }
    }
}
