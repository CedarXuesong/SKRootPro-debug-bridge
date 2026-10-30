using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SkpdbDesktop;

internal static class ModuleUpgrade
{
    // Used only after the temporary module passes device tests. Installer and
    // module keys go through loader stdin on the USB-facing address, never
    // shell arguments, command history or key files.
    public static async Task RunAsync(string host, string key)
    {
        host = IPAddress.Parse(host).ToString();
        const string id = "36c8b2dc7a8b4bb6a2b0e0ce54e05f31";
        const string loader = "/data/local/tmp/skpdb-look-lab/loader";
        using var control = new SkpdbClient();
        using var lab = new SkpdbClient();
        await control.ConnectAsync(new(host, Key: key));
        await lab.ConnectAsync(new(host, 40076, 40077, key));
        var old = await control.GetInfoAsync();
        var test = await lab.GetInfoAsync();
        if (old.Id32 != id || test.Id32 != id || test.Version != "0.12.6" ||
            test.Pid == old.Pid || test.ServicePort != 40076)
            throw new InvalidOperationException("Unexpected module identity; upgrade stopped");
        var dir = old.AdminSocket[..old.AdminSocket.LastIndexOf('/')];
        if (!Regex.IsMatch(dir, "^/data/[A-Za-z0-9_.-]+/modules/" + id + "$"))
            throw new InvalidOperationException("Unexpected installed module directory");
        var entries = await control.ListAsync(dir);
        Console.WriteLine("MODULE FILES " + string.Join(", ", entries.Select(e => e.Name)));
        var libraries = entries.Where(e => !e.IsDirectory && e.Name.EndsWith(".so")).ToArray();
        var library = libraries.SingleOrDefault(e => e.Name == "libskpdb_probe.so")
                      ?? entries.SingleOrDefault(e => !e.IsDirectory && e.Name == id)
                      ?? (libraries.Length == 1 ? libraries[0] : throw new InvalidOperationException("Ambiguous installed library"));
        var remoteLibrary = dir + "/" + library.Name;
        if (!(await control.ReadFileAsync(remoteLibrary, 0, 4)).AsSpan().SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }))
            throw new InvalidOperationException("Installed library is not an ELF binary");
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var backup = Path.GetFullPath(".scratch/module-backup/" + stamp);
        Directory.CreateDirectory(backup);
        await control.DownloadFileAsync(remoteLibrary, Path.Combine(backup, library.Name));
        byte[]? config = null;
        try { config = await control.ReadFileAsync(dir + "/skpdb.conf", 0, 65536); }
        catch (InvalidOperationException e) when (e.Message.Contains("No such file")) { }
        Console.WriteLine("BACKUP " + backup);
        var archive = "/data/local/tmp/skpdb_upgrade_" + stamp + ".zip";
        await lab.UploadFileAsync("skpdb_probe/skpdb_dev_network.zip", archive);
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync("skpdb_probe/skpdb_dev_network.zip"))).ToLowerInvariant();
        var hash = (await lab.ExecAsync("sha256sum " + archive)).Output.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (hash != expected) throw new InvalidOperationException("Uploaded archive checksum mismatch");

        async Task Load(string lib, string argument, string symbol, string tag)
        {
            var port = Random.Shared.Next(41000, 55000);
            var log = $"/data/local/tmp/skpdb_{tag}_{stamp}.log";
            var result = $"/data/local/tmp/skpdb_{tag}_{stamp}.rc";
            var started = await lab.ExecAsync($"nohup toybox nc -s {host} -l -p {port} -W 20 {loader} {lib} {argument} {symbol} {result} > {log} 2>&1 < /dev/null &");
            if (started.ExitCode != 0) throw new InvalidOperationException("Could not start loader");
            TcpClient? connected = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var candidate = new TcpClient();
                try { await candidate.ConnectAsync(host, port); connected = candidate; break; }
                catch (SocketException) { candidate.Dispose(); await Task.Delay(100); }
            }
            using var input = connected ?? throw new InvalidOperationException("Loader stdin not ready");
            await input.GetStream().WriteAsync(Encoding.UTF8.GetBytes(key + "\n"));
            for (var attempt = 0; attempt < 120; attempt++)
            {
                var status = (await lab.ExecAsync($"if test -f {result}; then cat {result}; fi")).Output.Trim();
                if (status.Length != 0)
                {
                    if (status != "result=0")
                    {
                        var detail = (await lab.ExecAsync("tail -c 1024 " + log)).Output.Replace(dir, "<module-dir>");
                        throw new InvalidOperationException("Loader failed: " + detail);
                    }
                    Console.WriteLine(tag.ToUpperInvariant() + " result=0");
                    return;
                }
                await Task.Delay(250);
            }
            throw new InvalidOperationException("Loader completion timed out");
        }

        await Load("/data/local/tmp/libskpdb_install.so", archive, "skpdb_install_once", "install");
        if (config is not null)
            using (var settings = new MemoryStream(config)) await lab.UploadFileAsync(settings, dir + "/skpdb.conf");
        var installedHash = (await lab.ExecAsync("sha256sum " + remoteLibrary)).Output.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync("skpdb_probe/libs/arm64-v8a/libskpdb_probe.so"))).ToLowerInvariant();
        if (installedHash != sourceHash) throw new InvalidOperationException("Installed module checksum mismatch");
        for (var slot = 0; slot < 10; slot++) await lab.TouchUpAsync(slot);
        // Remove the old renderer before its parent bridge exits. Older cursor
        // helpers did not stop on stdin EOF and could leave an occluding layer.
        await control.CursorDestroyAsync();
        var killed = await lab.ExecAsync("kill " + old.Pid);
        if (killed.ExitCode != 0) throw new InvalidOperationException("Could not stop old bridge");
        await Load(remoteLibrary, dir, "__skroot_module_main_18fdf393885363712349ef3de5411bd5", "activate");
        await Task.Delay(13500);
        using var current = new SkpdbClient();
        await current.ConnectAsync(new(host, Key: key));
        var active = await current.GetInfoAsync();
        if (active.Id32 != id || active.Version != "0.12.6" || active.ServicePort != 39876 ||
            active.Pid == old.Pid || active.Pid == test.Pid || active.FastMaxRecords != 16)
            throw new InvalidOperationException("New formal bridge verification failed");
        Console.WriteLine($"ACTIVE {active.Version} PID={active.Pid} PORT={active.ServicePort} RECORDS={active.FastMaxRecords}");
        Console.WriteLine("Temporary test bridge retained until final regression completes.");
        key = string.Empty;
    }
}
