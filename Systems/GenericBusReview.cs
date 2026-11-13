using System;
using System.IO;
using System.Security.Cryptography;
using CDLC.BusDriver.Interior.Models;
using Game.Prefabs;

namespace CDLC.BusDriver
{
    /// <summary>Review metadata for the built-in bus, without registering it as an authored vehicle.</summary>
    internal static class GenericBusReview
    {
        static VehicleProfile s_Profile;

        internal static VehicleProfile Profile(PrefabBase prefab) =>
            VehiclePacks.ForPrefab(prefab) ?? (prefab != null && prefab.name == OwnBus.Name ? Profile(BusInteriorSpec.Default) : null);

        internal static VehicleProfile Profile(BusInteriorSpec spec)
        {
            if (spec.ModelVariant != BusModelVariant.Generic ||
                !new BusInterior(spec).Key.Equals(new BusInterior(BusInteriorSpec.Default).Key)) return null;
            if (s_Profile != null) return s_Profile;
            VehicleProfile profile;
            using (Stream stream = Embedded(".vehicle.json"))
            using (var reader = new StreamReader(stream))
                profile = VehicleProfileJson.Parse(reader.ReadToEnd(), null, GenericBusMesh.Resource + ".vehicle.json", false);
            if (profile.Name != OwnBus.Name || !new BusInterior(profile.Spec).Key.Equals(new BusInterior(BusInteriorSpec.Default).Key))
                throw new InvalidDataException("Built-in review metadata differs from the embedded model.");
            return s_Profile = profile;
        }

        internal static string Resource(VehicleProfile profile) => profile.MeshPath ?? GenericBusMesh.Resource + ".swb2.gz";

        internal static Stream Open(VehicleProfile profile)
        {
            if (profile.MeshPath != null) return File.OpenRead(profile.MeshPath);
            if (profile.Name != OwnBus.Name || Profile(profile.Spec) == null) throw new InvalidDataException("No review runtime mesh.");
            return Embedded(".swb2.gz");
        }

        internal static string UvHash(VehicleProfile profile)
        {
            if (profile.MeshPath != null) return null;
            using (Stream stream = Embedded(".swu1.gz")) return Hash(stream);
        }

        internal static string Hash(Stream stream)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        static Stream Embedded(string suffix) => typeof(GenericBusMesh).Assembly.GetManifestResourceStream(GenericBusMesh.Resource + suffix) ??
            throw new FileNotFoundException("Missing built-in review resource: " + suffix);
    }
}
