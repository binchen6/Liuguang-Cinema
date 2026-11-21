using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TV.InstallerBuild;

public static class InstallerOutputIntegrity
{
    private const int FileObject = 1;
    private const uint LabelSecurityInformation = 0x10;
    private const byte SystemMandatoryLabelAceType = 0x11;
    private const int MediumIntegrity = 0x2000;

    public static bool IsBelowMedium(string path)
    {
        var error = GetNamedSecurityInfoW(path, FileObject, LabelSecurityInformation,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var sacl, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            if (sacl == IntPtr.Zero) return false;
            var aceCount = (ushort)Marshal.ReadInt16(sacl, 4);
            for (uint index = 0; index < aceCount; index++)
            {
                if (!GetAce(sacl, index, out var ace)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (Marshal.ReadByte(ace) != SystemMandatoryLabelAceType) continue;

                // SYSTEM_MANDATORY_LABEL_ACE contains a four-byte header, mask, then SID.
                var sid = new SecurityIdentifier(IntPtr.Add(ace, 8)).Value;
                var integrity = int.Parse(sid.Substring(sid.LastIndexOf('-') + 1), CultureInfo.InvariantCulture);
                if (integrity < MediumIntegrity) return true;
            }
            return false;
        }
        finally { LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetNamedSecurityInfoW(string path, int objectType, uint securityInformation,
        IntPtr owner, IntPtr group, IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr buffer);
}
