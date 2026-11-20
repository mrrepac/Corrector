using System.Security.Cryptography;
using System.Diagnostics;

namespace Corrector;

internal static class ReleaseUpdateSignatureChecks
{
    // Known answer for the seven UTF-8 bytes "fixture", signed with the public
    // P-256 test key in RFC 6979 A.2.5. Production never accepts this key.
    const string FixtureKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEYP7UuiVanTHJYet0xjVtaMBJuJI7Yfps5mliLmDyn7Z5A/4QCLi8maQa6elWKLxk8vGyDC1+n1F3o8KU1EYimQ==";
    const string FixtureSignature = "D+VHJrnDWdog+YAUgYB27PUH0i1xXJ6pEI850x3oB+Jovp+4e3veasp855EmHZhfD7eNqSFznmx/5gmaM7NWxA==";
    const string Digest = "f16d05ec6b29248d2c61adb1e9263f78e4f7bace1b955014a2d17872cfe4064d";

    internal static void Run(Action<string, bool> check)
    {
        var asset = new ReleaseUpdates.Asset("Corrector-Setup-2.3.4-win-x64.exe", 7, "sha256:" + Digest);
        var sidecar = new ReleaseUpdates.Asset(asset.Name + ".sig", 64,
            "sha256:d2e16a9282deb84758576a7fc7e5b1650d7b797d5e9c0b70995b059fd43d45c8");
        var current = new Version(2, 3, 3);
        var selected = ReleaseUpdates.Select("v2.3.4", [asset, sidecar], current)!;
        var release = selected with { SignatureBytes = Convert.FromBase64String(FixtureSignature) };
        bool Accepts(ReleaseUpdates.Update candidate, int size, string digest, string key = FixtureKey)
        {
            try { ReleaseUpdates.VerifySignature(candidate, size, digest, key); return true; }
            catch (InvalidDataException) { return false; }
        }
        bool Selects(params ReleaseUpdates.Asset[] assets)
        {
            try { return ReleaseUpdates.Select("v2.3.4", assets, current) != null; }
            catch (InvalidDataException) { return false; }
        }
        check("release signature verifies canonical manifest against fixture", Accepts(release, 7, Digest.ToUpperInvariant()));
        check("release requires its detached signature", !Selects(asset) && !Selects(asset, sidecar with { Name = "../setup.sig" }));
        check("release rejects an ambiguous signature asset", !Selects(asset, sidecar, sidecar));
        check("release requires signature length and GitHub digest", !Selects(asset, sidecar with { Size = 63 }) &&
            !Selects(asset, sidecar with { Digest = null }));
        check("missing or overlong signature bytes are rejected", !Accepts(selected, 7, Digest) &&
            !Accepts(release with { SignatureBytes = [.. release.SignatureBytes, 0] }, 7, Digest));
        check("unsigned change to installer size is rejected", !Accepts(release with { Installer = asset with { Size = 8 } }, 8, Digest));
        string changedDigest = new string('9', 64);
        check("unsigned change to installer digest is rejected", !Accepts(release with { Installer = asset with { Digest = "sha256:" + changedDigest } }, 7, changedDigest));
        check("unsigned change to release version and name is rejected", !Accepts(release with
        {
            Version = new Version(2,3,5),
            Installer = asset with { Name = "Corrector-Setup-2.3.5-win-x64.exe" },
            Signature = sidecar with { Name = "Corrector-Setup-2.3.5-win-x64.exe.sig" }
        }, 7, Digest));
        check("wrong installer basename is rejected", !Accepts(release with { Installer = asset with { Name = "../Corrector.exe" } }, 7, Digest));
        check("signature GitHub digest is checked", !Accepts(release with { Signature = sidecar with { Digest = "sha256:" + Digest } }, 7, Digest));
        byte[] altered = (byte[])release.SignatureBytes.Clone();
        altered[0] ^= 1;
        var alteredSignature = sidecar with { Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(altered)) };
        check("modified signature with matching GitHub digest is rejected",
            !Accepts(release with { SignatureBytes = altered, Signature = alteredSignature }, 7, Digest));
        check("release pin rejects public RFC test signer", !Accepts(release, 7, Digest, ReleaseSigningKey.PublicKeySpki));
        CheckInstallerHandoff(check);
    }

    static void CheckInstallerHandoff(Action<string, bool> check)
    {
        // Redirected stdin keeps this inert child waiting for the test; closing it
        // or terminating the process cannot install anything or touch application data.
        static Process StartChild() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/D /Q")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Environment.SystemDirectory
        })!;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset);
        using (var child = StartChild())
        {
            child.StandardInput.Close();
            child.WaitForExit(5000);
            try
            {
                ReleaseUpdates.WaitForInstaller(ready, child, () => true, TimeSpan.Zero, default).GetAwaiter().GetResult();
                check("failed child cannot trigger handoff", false);
            }
            catch (IOException) { check("failed child cannot trigger handoff", child.HasExited); }
        }
        using (var child = StartChild())
        {
            try
            {
                ReleaseUpdates.WaitForInstaller(ready, child, () => true, TimeSpan.Zero, default).GetAwaiter().GetResult();
                check("unacknowledged child times out", false);
            }
            catch (TimeoutException) { check("unacknowledged child times out", child.WaitForExit(5000)); }
        }
        using (var child = StartChild())
        {
            try
            {
                ready.Set();
                int probes = 0;
                // Acknowledged installers may outlive the readiness deadline while a user is busy.
                Task.Run(() => ReleaseUpdates.WaitForInstaller(ready, child, () => ++probes == 3,
                    TimeSpan.Zero, default)).GetAwaiter().GetResult();
                check("acknowledged installer waits for idle", probes == 3 && !child.HasExited);
                using var cancel = new CancellationTokenSource();
                try
                {
                    Task.Run(() => ReleaseUpdates.WaitForInstaller(ready, child, () => { cancel.Cancel(); return false; },
                        TimeSpan.FromSeconds(5), cancel.Token)).GetAwaiter().GetResult();
                    check("cancelled handoff stops child", false);
                }
                catch (OperationCanceledException) { check("cancelled handoff stops child", child.WaitForExit(5000)); }
            }
            finally { if (!child.HasExited) child.Kill(); }
        }
    }
}
