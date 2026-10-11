using Microsoft.Win32;

namespace SteeringWheelEmulator.VirtualHid;

/// <summary>
/// Ensures physical Fanatec DD1 native PC (<c>VID_0EB7&amp;PID_0006</c>) has DirectInput OEM
/// ForceFeedback wired to Fanatec's <c>FAWForceFeedbackDrv</c> — same CLSID Fanatec registers
/// for DD2 (<c>PID_0007</c>) and other bases.
/// <para>
/// Without this tree, DI reports no FFB / ConstantForce → not implemented, even though
/// Fanatec Control Panel still drives the motor on its proprietary path. Emulator torque
/// out needs the FAW EffectDriver so native PC mode can stay selected (no PC Comp switch).
/// </para>
/// Ported from the working Desktop fork; called when registering virtual PC Comp OEM so
/// sibling Fanatec keys never leave physical 0006 without FAW.
/// </summary>
public static class FanatecPhysicalFfbOem
{
    /// <summary>Fanatec FAWForceFeedback Class (FAWForceFeedbackDrv64.dll).</summary>
    public const string FawClsid = "{3CCED46F-EDBC-4AB5-9AA1-1D19769B1313}";

    private const string OemParent =
        @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";

    private const string Dd1PcKey = "VID_0EB7&PID_0006";
    private const string TemplateKey = "VID_0EB7&PID_0007";

    public static string? LastMessage { get; private set; }

    /// <summary>
    /// Install / repair FAW OEM FFB for physical DD1 PC. Safe to call on every Start.
    /// </summary>
    public static string EnsureDd1PcForceFeedback()
    {
        var parts = new List<string>();
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                parts.Add(EnsureOnRoot(root));
            }
            catch (Exception ex)
            {
                parts.Add($"{root.Name}: {ex.Message}");
            }
        }

        LastMessage = string.Join(" | ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
        return LastMessage;
    }

    private static string EnsureOnRoot(RegistryKey root)
    {
        using var parent = root.OpenSubKey(OemParent, writable: true)
                        ?? root.CreateSubKey(OemParent, writable: true);
        if (parent is null)
            return $"{root.Name}: OEM parent unavailable.";

        using var target = parent.CreateSubKey(Dd1PcKey, writable: true);
        if (target is null)
            return $"{root.Name}: cannot open {Dd1PcKey}.";

        // Prefer cloning Fanatec's complete DD2 template when present (same FAW CLSID).
        using var template = parent.OpenSubKey(TemplateKey, writable: false);
        if (template is not null &&
            template.OpenSubKey("OEMForceFeedback") is { } tmplFf)
        {
            using (tmplFf)
            {
                CopyOemForceFeedback(template, target);
                EnsureFfbOemDataBit(target);
                return $"{root.Name}: FAW OEM FFB for {Dd1PcKey} cloned from {TemplateKey}.";
            }
        }

        WriteMinimalFawTree(target);
        EnsureFfbOemDataBit(target);
        return $"{root.Name}: FAW OEM FFB for {Dd1PcKey} written (minimal; no {TemplateKey} template).";
    }

    private static void EnsureFfbOemDataBit(RegistryKey oem)
    {
        // Working Fanatec FFB bases use OEMData[0] with bit 0x40 set (e.g. 0x43).
        // DD1 PC often ships as 0x03 — DI then omits ForceFeedback even if FAW is registered.
        if (oem.GetValue("OEMData") is not byte[] data || data.Length < 1)
        {
            oem.SetValue("OEMData", new byte[] { 0x43, 0x00, 0x88, 0x10, 0x13, 0x00, 0x00, 0x00 },
                RegistryValueKind.Binary);
            return;
        }

        if ((data[0] & 0x40) != 0)
            return;

        var copy = (byte[])data.Clone();
        copy[0] = (byte)(copy[0] | 0x40);
        oem.SetValue("OEMData", copy, RegistryValueKind.Binary);
    }

    private static void CopyOemForceFeedback(RegistryKey template, RegistryKey target)
    {
        if (template.GetValue("ConfigCLSID") is string cfg)
            target.SetValue("ConfigCLSID", cfg, RegistryValueKind.String);

        using var srcFf = template.OpenSubKey("OEMForceFeedback", writable: false);
        if (srcFf is null) return;

        target.DeleteSubKeyTree("OEMForceFeedback", throwOnMissingSubKey: false);
        using var dstFf = target.CreateSubKey("OEMForceFeedback", writable: true);
        if (dstFf is null) return;

        CopyValues(srcFf, dstFf);
        CopyTree(srcFf, dstFf);

        using var srcAxis = template.OpenSubKey(@"Axes\0", writable: false);
        if (srcAxis is null) return;
        using var dstAxis = target.CreateSubKey(@"Axes\0", writable: true);
        if (dstAxis is null) return;
        if (dstAxis.GetValue("") is null && srcAxis.GetValue("") is string name)
            dstAxis.SetValue("", name, RegistryValueKind.String);
        if (srcAxis.GetValue("Attributes") is byte[] attrs)
            dstAxis.SetValue("Attributes", attrs, RegistryValueKind.Binary);
        if (srcAxis.GetValue("FFAttributes") is byte[] ffAttrs)
            dstAxis.SetValue("FFAttributes", ffAttrs, RegistryValueKind.Binary);
    }

    private static void WriteMinimalFawTree(RegistryKey oem)
    {
        using var ff = oem.CreateSubKey("OEMForceFeedback", writable: true);
        if (ff is null) return;

        ff.SetValue("CLSID", FawClsid, RegistryValueKind.String);
        ff.SetValue("Attributes",
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0xE8, 0x03, 0x00, 0x00, 0xE8, 0x03, 0x00, 0x00 },
            RegistryValueKind.Binary);

        WriteEffect(ff, "{13541C20-8E33-11D0-9AD0-00A0C9A06E35}", "Constant",
            [0x00, 0x00, 0x00, 0x00, 0x01, 0x86, 0x00, 0x00, 0xED, 0x03, 0x00, 0x00, 0xED, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
        WriteEffect(ff, "{13541C27-8E33-11D0-9AD0-00A0C9A06E35}", "Spring",
            [0x07, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
        WriteEffect(ff, "{13541C28-8E33-11D0-9AD0-00A0C9A06E35}", "Damper",
            [0x08, 0x00, 0x00, 0x00, 0x04, 0xD8, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x6D, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);
        WriteEffect(ff, "{13541C23-8E33-11D0-9AD0-00A0C9A06E35}", "Sine Wave",
            [0x03, 0x00, 0x00, 0x00, 0x03, 0x86, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0xEF, 0x03, 0x00, 0x00, 0x30, 0x00, 0x00, 0x00]);

        using var axis = oem.CreateSubKey(@"Axes\0", writable: true);
        if (axis is null) return;
        axis.SetValue("", "Wheel Axis", RegistryValueKind.String);
        axis.SetValue("Attributes", new byte[] { 0x01, 0x81, 0x00, 0x00 }, RegistryValueKind.Binary);
        axis.SetValue("FFAttributes", new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00 },
            RegistryValueKind.Binary);
    }

    private static void WriteEffect(RegistryKey ffRoot, string effectClsid, string name, byte[] attributes)
    {
        using var effect = ffRoot.CreateSubKey($@"Effects\{effectClsid}", writable: true);
        if (effect is null) return;
        effect.SetValue("", name, RegistryValueKind.String);
        effect.SetValue("Attributes", attributes, RegistryValueKind.Binary);
    }

    private static void CopyValues(RegistryKey src, RegistryKey dst)
    {
        foreach (var name in src.GetValueNames())
        {
            var kind = src.GetValueKind(name);
            var val = src.GetValue(name);
            if (val is not null)
                dst.SetValue(name, val, kind);
        }
    }

    private static void CopyTree(RegistryKey src, RegistryKey dst)
    {
        foreach (var subName in src.GetSubKeyNames())
        {
            using var srcSub = src.OpenSubKey(subName, writable: false);
            if (srcSub is null) continue;
            using var dstSub = dst.CreateSubKey(subName, writable: true);
            if (dstSub is null) continue;
            CopyValues(srcSub, dstSub);
            CopyTree(srcSub, dstSub);
        }
    }
}
