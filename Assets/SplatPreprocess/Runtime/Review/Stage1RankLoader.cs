using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SplatPreprocess
{
    [Serializable]
    public sealed class RankManifest
    {
        public int schema_version;
        public string source_hash, scene_hash, rank_id, rank_sha256, rank_path, importance_path;
        public int source_count, eligible_count;
        public int EligibleCount => eligible_count;

        public void Validate(string expectedSourceHash, string expectedSceneHash)
        {
            if (schema_version != 1 || source_count <= 0 || eligible_count < 0 || eligible_count > source_count)
                throw new InvalidDataException("Unsupported or inconsistent rank manifest");
            if (!IsHash(source_hash) || !IsHash(scene_hash) || !IsHash(rank_sha256) || string.IsNullOrWhiteSpace(rank_id))
                throw new InvalidDataException("Ranking identities or SHA-256 are missing");
            if (source_hash != expectedSourceHash || scene_hash != expectedSceneHash)
                throw new InvalidDataException("Ranking source or scene does not match the explicit scene binding");
            if (string.IsNullOrWhiteSpace(rank_path) || Path.GetFileName(rank_path) != rank_path || rank_path == "." || rank_path == "..")
                throw new InvalidDataException("rank_path must name a file beside its manifest");
        }

        static bool IsHash(string value) => Regex.IsMatch(value ?? "", "^[a-f0-9]{64}$");
    }

    public sealed class Stage1LoadedRank
    {
        public RankManifest Manifest { get; }
        public uint[] Order { get; }
        internal Stage1LoadedRank(RankManifest manifest, uint[] order) { Manifest = manifest; Order = order; }
    }

    public static class Stage1RankLoader
    {
        public static Stage1LoadedRank Load(string manifestPath, string expectedSourceHash, string expectedSceneHash)
        {
            var fullPath = Path.GetFullPath(manifestPath);
            var manifest = JsonUtility.FromJson<RankManifest>(File.ReadAllText(fullPath));
            if (manifest == null) throw new InvalidDataException("Ranking manifest is empty");
            manifest.Validate(expectedSourceHash, expectedSceneHash);
            var path = Path.Combine(Path.GetDirectoryName(fullPath), manifest.rank_path);
            if (new FileInfo(path).Length != (long)manifest.eligible_count * 4)
                throw new InvalidDataException("Rank file length does not match eligible_count");
            var bytes = File.ReadAllBytes(path);
            using (var sha = SHA256.Create())
            {
                var actualHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                if (actualHash != manifest.rank_sha256) throw new InvalidDataException("Rank file SHA-256 does not match its manifest");
            }
            var order = new uint[manifest.eligible_count];
            if (BitConverter.IsLittleEndian) Buffer.BlockCopy(bytes, 0, order, 0, bytes.Length);
            else for (var i = 0; i < order.Length; i++)
            {
                var offset = 4 * i;
                order[i] = (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
            }
            ValidateOrder(manifest, order);
            return new Stage1LoadedRank(manifest, order);
        }

        public static void ValidateOrder(RankManifest manifest, uint[] order)
        {
            if (order == null || order.Length != manifest.eligible_count) throw new InvalidDataException("Rank count does not match its manifest");
            var seen = new bool[manifest.source_count];
            foreach (var id in order)
            {
                if (id >= manifest.source_count) throw new InvalidDataException("Rank contains an out-of-range original source ID");
                if (seen[id]) throw new InvalidDataException("Rank contains a duplicate original source ID");
                seen[id] = true;
            }
            // The public in-memory upload path must prove the same frozen rank identity as the file loader.
            // Hash little-endian bytes in bounded blocks instead of retaining another full rank-sized array.
            using (var sha = SHA256.Create())
            {
                var bytes = new byte[65536];
                for (var start = 0; start < order.Length; start += bytes.Length / 4)
                {
                    var count = Math.Min(bytes.Length / 4, order.Length - start);
                    for (var i = 0; i < count; i++)
                    {
                        var id = order[start + i];
                        var offset = i * 4;
                        bytes[offset] = (byte)id;
                        bytes[offset + 1] = (byte)(id >> 8);
                        bytes[offset + 2] = (byte)(id >> 16);
                        bytes[offset + 3] = (byte)(id >> 24);
                    }
                    sha.TransformBlock(bytes, 0, count * 4, bytes, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (hash != manifest.rank_sha256) throw new InvalidDataException("Rank order SHA-256 does not match its frozen identity");
            }
        }
    }
}
