using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SplatPreprocess
{
    public static class Stage1Counts
    {
        public static int KeepCount(int sourceCount, int centiPercent)
        {
            if (sourceCount < 0) throw new ArgumentOutOfRangeException(nameof(sourceCount));
            if (centiPercent < 0 || centiPercent > 10000) throw new ArgumentOutOfRangeException(nameof(centiPercent));
            return (int)((long)sourceCount * centiPercent / 10000);
        }
    }

    [Serializable]
    public sealed class SourceManifest
    {
        public int schema_version;
        public string source_path, sha256, id_rule, encoding, quaternion_order, scale_encoding, opacity_encoding, calibration_status;
        public long byte_length;
        public int vertex_count, data_offset, record_bytes, sh_degree;
        public SourceProperty[] properties;
        public void Validate()
        {
            if (schema_version != 1 || id_rule != "zero_based_vertex_row" || encoding != "binary_little_endian" ||
                quaternion_order != "wxyz" || scale_encoding != "natural_log" || opacity_encoding != "logit")
                throw new InvalidDataException("Unsupported source manifest schema or conventions");
            if (string.IsNullOrWhiteSpace(source_path) || !Regex.IsMatch(sha256 ?? "", "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Source path or SHA-256 is missing");
            if (vertex_count < 0 || data_offset <= 0 || record_bytes <= 0 || sh_degree < 0 || sh_degree > 3 ||
                byte_length != data_offset + (long)vertex_count * record_bytes)
                throw new InvalidDataException("Source byte layout is inconsistent");
            if (properties == null || properties.Length * 4L != record_bytes ||
                properties.Any(p => p == null || string.IsNullOrEmpty(p.name) || p.scalar_type != "float32") ||
                properties.Select(p => p.name).Distinct().Count() != properties.Length ||
                properties.Where((p, i) => p.byte_offset != 4 * i).Any())
                throw new InvalidDataException("Source property layout is inconsistent");
            var required = new[] { "x", "y", "z", "rot_0", "rot_1", "rot_2", "rot_3", "scale_0", "scale_1", "scale_2", "opacity", "f_dc_0", "f_dc_1", "f_dc_2" };
            if (required.Except(properties.Select(p => p.name)).Any())
                throw new InvalidDataException("Source Gaussian attributes are missing");
        }
    }

    [Serializable]
    public sealed class SourceProperty
    {
        public string name, scalar_type;
        public int byte_offset;
    }
}
