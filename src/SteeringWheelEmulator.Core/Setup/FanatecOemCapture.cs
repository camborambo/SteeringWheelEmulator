using System.IO.Compression;
using System.Text;
using SteeringWheelEmulator.Core.Input;
using SteeringWheelEmulator.Core.Models;
using Microsoft.Win32;
using SharpDX.DirectInput;

namespace SteeringWheelEmulator.Core.Setup;

/// <summary>
/// Captures physical Fanatec DirectInput OEM registry trees + DI object lists so a
/// user with a real base can send Dial/handbrake / joy.cpl faces for emulator matching.
/// </summary>
public static class FanatecOemCapture
{
    private const string OemParent =
        @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";

    /// <summary>Known Fanatec DD1 / sibling PIDs we always probe (even if the key is missing).</summary>
    private static readonly string[] PriorityOemKeys =
    [
        "VID_0EB7&PID_0004", // PC Comp
        "VID_0EB7&PID_0006", // PC / ClubSport-style
        "VID_0EB7&PID_0007", // DD2 template often used for FAW clone
        "VID_0EB7&PID_0F50", // Xbox mode hardware
        "VID_0EB7&PID_0F54", // legacy Xbox guess
    ];

    /// <summary>
    /// Writes a focused zip (OEM registry + Fanatec DI devices) for support / research.
    /// Does not require a Debug session.
    /// </summary>
    public static string ExportToZip(string destinationZipPath, string appVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        var staging = Path.Combine(Path.GetTempPath(), "steeringwheel-emulator-fanatec-oem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            WriteReadme(staging);
            WriteSummary(staging, appVersion);
            WriteOemTrees(staging);
            WriteDirectInputFanatec(staging);

            var dir = Path.GetDirectoryName(destinationZipPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(destinationZipPath))
                File.Delete(destinationZipPath);

            ZipFile.CreateFromDirectory(staging, destinationZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return destinationZipPath;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>Also embed into the full diagnostics zip when Export log runs.</summary>
    public static void WriteIntoStaging(string staging)
    {
        var sub = Path.Combine(staging, "fanatec-oem-capture");
        Directory.CreateDirectory(sub);
        WriteReadme(sub);
        WriteSummary(sub, AppVersion.Display);
        WriteOemTrees(sub);
        WriteDirectInputFanatec(sub);
    }

    private static void WriteReadme(string staging)
    {
        File.WriteAllText(
            Path.Combine(staging, "HOW-TO-SEND.txt"),
            "Fanatec physical OEM capture\r\n" +
            "============================\r\n\r\n" +
            "Attach this zip to a GitHub issue:\r\n" +
            "  " + DiagnosticsExporter.GitHubIssuesUrl + "\r\n\r\n" +
            "How to capture (on a PC with a physical Fanatec base):\r\n" +
            "  1. Plug in the Fanatec wheel base (True Drive can be open or closed — note which).\r\n" +
            "  2. Prefer Emulator Stopped so virtual Fanatec OEM trees are not mixed in.\r\n" +
            "  3. Status bar → Debug Tools → Capture Fanatec OEM… → save the zip.\r\n" +
            "  4. Attach the zip and say: base model, USB mode (PC / PC Comp / Xbox), True Drive on/off.\r\n\r\n" +
            "Files:\r\n" +
            "  summary.txt              - machine / app version / tip\r\n" +
            "  oem-registry-fanatec.txt - HKCU+HKLM Joystick\\OEM\\VID_0EB7&PID_* trees\r\n" +
            "  di-fanatec-devices.txt   - DirectInput Fanatec devices + object names (axes)\r\n",
            Encoding.UTF8);
    }

    private static void WriteSummary(string staging, string appVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Fanatec physical OEM capture");
        sb.AppendLine("Generated (local): " + DateTime.Now.ToString("o"));
        sb.AppendLine("Machine: " + Environment.MachineName);
        sb.AppendLine("App version: " + appVersion);
        sb.AppendLine("OS: " + Environment.OSVersion);
        sb.AppendLine();
        sb.AppendLine("Goal: learn real OEM axis names (esp. Dial/handbrake) and joy.cpl faces");
        sb.AppendLine("for VID_0EB7 PID_0004 / 0006 so the virtual Fanatec can match.");
        sb.AppendLine();
        sb.AppendLine("Tip: capture once in native PC mode and once in PC Comp if the base can switch.");
        File.WriteAllText(Path.Combine(staging, "summary.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteOemTrees(string staging)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Fanatec Joystick OEM registry (HKCU + HKLM)");
        sb.AppendLine("Path: " + OemParent + @"\VID_0EB7&PID_*");
        sb.AppendLine();

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            sb.AppendLine("======== " + root.Name + " ========");
            sb.AppendLine();
            try
            {
                using var parent = root.OpenSubKey(OemParent);
                if (parent is null)
                {
                    sb.AppendLine("(OEM parent missing)");
                    sb.AppendLine();
                    continue;
                }

                var found = parent.GetSubKeyNames()
                    .Where(n => n.StartsWith("VID_0EB7&", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var keys = PriorityOemKeys
                    .Concat(found)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                sb.AppendLine("VID_0EB7 keys present: " +
                              (found.Count == 0 ? "(none)" : string.Join(", ", found)));
                sb.AppendLine();

                foreach (var keyName in keys)
                {
                    using var key = parent.OpenSubKey(keyName);
                    if (key is null)
                    {
                        sb.AppendLine("--- " + keyName + " (missing) ---");
                        sb.AppendLine();
                        continue;
                    }

                    sb.AppendLine("--- " + keyName + " ---");
                    DumpKeyRecursive(sb, key, indent: "");
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Read failed: " + ex.Message);
                sb.AppendLine();
            }
        }

        File.WriteAllText(Path.Combine(staging, "oem-registry-fanatec.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static void DumpKeyRecursive(StringBuilder sb, RegistryKey key, string indent)
    {
        // Prefer default value first (DI effect/axis names).
        try
        {
            var def = key.GetValue("");
            if (def is not null)
                sb.AppendLine($"{indent}(Default) = {FormatRegValue(def)}");
        }
        catch { /* ignore */ }

        foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(name))
                continue;
            try
            {
                sb.AppendLine($"{indent}{name} = {FormatRegValue(key.GetValue(name))}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{indent}{name} = (read failed: {ex.Message})");
            }
        }

        foreach (var subName in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"{indent}[{subName}]");
            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub is null)
                {
                    sb.AppendLine($"{indent}  (unavailable)");
                    continue;
                }

                DumpKeyRecursive(sb, sub, indent + "  ");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{indent}  (open failed: {ex.Message})");
            }
        }
    }

    private static void WriteDirectInputFanatec(string staging)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DirectInput Fanatec-looking devices + object names");
        sb.AppendLine("Generated: " + DateTime.Now.ToString("o"));
        sb.AppendLine();
        sb.AppendLine("Look for Dial / Slider1 / Handbrake-style axis names on physical bases.");
        sb.AppendLine("Virtual emulator nodes (REV_E001) are marked when detected.");
        sb.AppendLine();

        try
        {
            using var di = new DirectInput();
            var all = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly).ToList();
            var fanatec = all.Where(LooksLikeFanatecInstance).ToList();
            sb.AppendLine("Attached GameControl devices: " + all.Count);
            sb.AppendLine("Fanatec-looking: " + fanatec.Count);
            sb.AppendLine();

            if (fanatec.Count == 0)
            {
                sb.AppendLine("No Fanatec-named / 0EB7 product GUID devices attached.");
                sb.AppendLine("Plug the physical base and capture again.");
            }

            var i = 0;
            foreach (var instance in fanatec)
            {
                i++;
                sb.AppendLine($"[{i}] {instance.InstanceName}");
                sb.AppendLine($"    Product: {instance.ProductName}");
                sb.AppendLine($"    InstanceGuid: {instance.InstanceGuid:D}");
                sb.AppendLine($"    ProductGuid:  {instance.ProductGuid:D}");
                var data1 = BitConverter.ToInt32(instance.ProductGuid.ToByteArray(), 0);
                var vid = data1 & 0xFFFF;
                var pid = (data1 >> 16) & 0xFFFF;
                sb.AppendLine($"    VID/PID from ProductGuid: {vid:X4}:{pid:X4}");
                var maybeVirtual =
                    VirtualDeviceIdentity.LooksLikeEmulatedVirtualName(
                        instance.InstanceName, EmulatedDeviceKind.FanatecDd1PcComp) ||
                    VirtualDeviceIdentity.LooksLikeEmulatedVirtualName(
                        instance.ProductName, EmulatedDeviceKind.FanatecDd1PcComp);
                sb.AppendLine($"    Name looks like emulator Fanatec: {maybeVirtual}");

                try
                {
                    using var joy = new Joystick(di, instance.InstanceGuid);
                    var caps = joy.Capabilities;
                    sb.AppendLine(
                        $"    Caps: FFB={caps.Flags.HasFlag(DeviceFlags.ForceFeedback)} " +
                        $"axes={caps.AxeCount} buttons={caps.ButtonCount} hats={caps.PovCount}");

                    joy.Acquire();
                    try
                    {
                        var objects = joy.GetObjects()
                            .OrderBy(o => o.Offset)
                            .ThenBy(o => o.Name ?? "", StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        sb.AppendLine($"    Objects ({objects.Count}):");
                        foreach (var o in objects)
                        {
                            sb.AppendLine(
                                $"      offset={o.Offset}  name={o.Name}  objectType={o.ObjectType}");
                        }
                    }
                    finally
                    {
                        try { joy.Unacquire(); } catch { /* ignore */ }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("    Open/GetObjects failed: " + ex.Message);
                }

                sb.AppendLine();
            }

            // Also list non-Fanatec FFB for context (Simucube etc.).
            sb.AppendLine("=== Other FFB devices (context) ===");
            foreach (var row in InputHub.EnumerateAllAttachedForDiagnostics().Where(r => r.SupportsForceFeedback))
            {
                if (row.Name.Contains("FANATEC", StringComparison.OrdinalIgnoreCase) ||
                    row.ProductName.Contains("FANATEC", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.AppendLine($"  - {row.Name} (FFB axes={row.AxisCount} buttons={row.ButtonCount})");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("Enumerate failed: " + ex.Message);
        }

        File.WriteAllText(Path.Combine(staging, "di-fanatec-devices.txt"), sb.ToString(), Encoding.UTF8);
    }

    private static bool LooksLikeFanatecInstance(DeviceInstance instance)
    {
        var name = (instance.InstanceName ?? "") + " " + (instance.ProductName ?? "");
        if (name.Contains("FANATEC", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Podium Wheel", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ClubSport", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var data1 = BitConverter.ToInt32(instance.ProductGuid.ToByteArray(), 0);
            var vid = data1 & 0xFFFF;
            return vid == FanatecDd1Identity.VendorId;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatRegValue(object? value) => value switch
    {
        null => "(null)",
        string[] multi => string.Join(" | ", multi),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => value.ToString() ?? "",
    };
}
