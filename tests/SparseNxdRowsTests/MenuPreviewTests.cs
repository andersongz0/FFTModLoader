using GenericJobs;
using System.Runtime.InteropServices;

internal static class MenuPreviewTests
{
    internal static void Run()
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
        static int? Lookup(byte job, byte sex) => job switch
        {
            0xA2 => sex == 0x80 ? 170 : 171,
            0xA3 => sex == 0x80 ? 172 : 173,
            _ => null
        };
        nint menu = Marshal.AllocHGlobal(0x4A60);
        nint descriptor = Marshal.AllocHGlobal(0x94);
        try
        {
            byte[] menuBytes = Enumerable.Repeat((byte)0xA5, 0x4A60).ToArray();
            Marshal.Copy(menuBytes, 0, menu, menuBytes.Length);
            // Named and generic shapes must both display the job's preview.
            // All flag bits except native bit 6 are irrelevant to gender.
            foreach (byte flags in Enumerable.Range(0, 256).Select(i => (byte)i))
            {
                Marshal.WriteByte(menu, MenuPreviewPolicy.FlagsOffset, flags);
                byte sex = (flags & 0x40) == 0 ? (byte)0x80 : (byte)0x81;
                Require(MenuPreviewPolicy.SexFromFlags(flags) == sex, "Native gender bit changed");
                foreach (int previous in Enumerable.Range(0, 174))
                foreach (byte job in new byte[] {0xA2, 0xA3})
                {
                    byte[] before = Enumerable.Repeat((byte)0xA5, 0x94).ToArray();
                    BitConverter.GetBytes((int)job).CopyTo(before, MenuPreviewPolicy.JobOffset);
                    BitConverter.GetBytes(previous).CopyTo(before, MenuPreviewPolicy.ShapeOffset);
                    Marshal.Copy(before, 0, descriptor, before.Length);
                    Require(MenuPreviewPolicy.TryRewrite(menu, descriptor, Lookup), "Registered preview skipped");
                    byte[] expected = (byte[])before.Clone();
                    BitConverter.GetBytes(Lookup(job, sex)!.Value).CopyTo(expected, MenuPreviewPolicy.ShapeOffset);
                    expected[MenuPreviewPolicy.PaletteOffset] = expected[MenuPreviewPolicy.PaletteOffset + 1] = 0;
                    byte[] actual = new byte[before.Length];
                    Marshal.Copy(descriptor, actual, 0, actual.Length);
                    Require(actual.SequenceEqual(expected), "Preview changed unrelated descriptor fields");
                }
            }
            foreach (int job in Enumerable.Range(-1, 258))
            {
                if (job is 0xA2 or 0xA3) continue;
                byte[] before = Enumerable.Repeat((byte)0xA5, 0x94).ToArray();
                BitConverter.GetBytes(job).CopyTo(before, MenuPreviewPolicy.JobOffset);
                Marshal.Copy(before, 0, descriptor, before.Length);
                Require(!MenuPreviewPolicy.TryRewrite(menu, descriptor, Lookup), "Unregistered/invalid job changed");
                byte[] actual = new byte[before.Length];
                Marshal.Copy(descriptor, actual, 0, actual.Length);
                Require(actual.SequenceEqual(before), "Unregistered descriptor modified");
            }
            Marshal.WriteInt32(descriptor, MenuPreviewPolicy.JobOffset, 0xA2);
            Require(!MenuPreviewPolicy.TryRewrite(menu, descriptor, (job, sex) => null), "Missing gender asset substituted");
            Require(!MenuPreviewPolicy.TryRewrite(0, descriptor, Lookup), "Null menu accepted");
            Require(!MenuPreviewPolicy.TryRewrite(menu, 0, Lookup), "Null descriptor accepted");
            menuBytes[MenuPreviewPolicy.FlagsOffset] = 255;
            byte[] menuAfter = new byte[menuBytes.Length];
            Marshal.Copy(menu, menuAfter, 0, menuAfter.Length);
            Require(menuAfter.SequenceEqual(menuBytes), "Selected unit/menu flags changed");
            Console.WriteLine("PASS: 89088 registered previews, all native/custom prior shapes and 256 flags, both jobs/sexes; exact six-byte write scope; native/unregistered jobs, missing assets, nulls and menu data preserved.");
        }
        finally
        {
            Marshal.FreeHGlobal(descriptor);
            Marshal.FreeHGlobal(menu);
        }
    }
}
