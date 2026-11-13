using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using NativeCompressions;

/// <summary>
/// Drives NativeCompressions' <see cref="LZ4Stream"/> the way the engine does, for upgrade checks
/// outside Unity. Commands:
/// <list type="bullet">
/// <item><c>info</c> — prints the loaded library assembly's full name.</item>
/// <item><c>compress-dir IN OUT copy|small</c> — every <c>IN/*.bin</c> to <c>OUT/*.lz4</c>.</item>
/// <item><c>decompress-dir IN OUT copy|small</c> — every <c>IN/*.lz4</c> to <c>OUT/*.bin</c>.</item>
/// <item><c>fail FILE SECONDS</c> — decompresses one file and prints <c>read=…</c>/<c>dispose=…</c>, or <c>HANG</c>.</item>
/// </list>
/// <c>small</c> mirrors <c>BinaryWriter</c>/<c>BinaryReader</c>: many tiny writes or reads of varying size.
/// </summary>
internal static class Program
{
    private const int EXIT_BAD_ARGS = 2;
    private const int EXIT_HANG = 3;

    private static readonly int[] s_chunkSizes = { 1, 4, 2, 13, 8, 1, 4096, 3, 17, 512 };

    private static int Main(string[] args)
    {
        if (args.Length == 0) return EXIT_BAD_ARGS;
        switch (args[0])
        {
            case "info" when args.Length == 1:
                Console.WriteLine(typeof(LZ4Stream).Assembly.FullName);
                return 0;
            case "compress-dir" when args.Length == 4:
                return ForEach(args[1], "*.bin", args[2], ".lz4", f => Compress(File.ReadAllBytes(f), args[3] == "small"));
            case "decompress-dir" when args.Length == 4:
                return ForEach(args[1], "*.lz4", args[2], ".bin", f => Decompress(File.ReadAllBytes(f), args[3] == "small"));
            case "fail" when args.Length == 3:
                return Fail(args[1], int.Parse(args[2]));
            default:
                Console.Error.WriteLine("usage: info | compress-dir IN OUT copy|small | decompress-dir IN OUT copy|small | fail FILE SECONDS");
                return EXIT_BAD_ARGS;
        }
    }

    /// <summary>Applies <paramref name="transform"/> to every matching file, printing <c>done ok=N err=M</c>.</summary>
    private static int ForEach(string inDir, string pattern, string outDir, string outExtension, Func<string, byte[]> transform)
    {
        Directory.CreateDirectory(outDir);
        int ok = 0, failed = 0;
        foreach (string file in Directory.GetFiles(inDir, pattern))
        {
            try
            {
                File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + outExtension), transform(file));
                ok++;
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"ERR {Path.GetFileName(file)}: {e.GetType().Name}: {e.Message}");
            }
        }

        Console.WriteLine($"done ok={ok} err={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static byte[] Compress(byte[] data, bool small)
    {
        using MemoryStream output = new MemoryStream();
        using (LZ4Stream lz4 = new LZ4Stream(output, CompressionMode.Compress, true))
        {
            if (!small)
            {
                lz4.Write(data, 0, data.Length);
            }
            else
            {
                for (int pos = 0, i = 0; pos < data.Length; i++)
                {
                    int count = Math.Min(s_chunkSizes[i % s_chunkSizes.Length], data.Length - pos);
                    lz4.Write(data, pos, count);
                    pos += count;
                }
            }
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data, bool small)
    {
        using MemoryStream input = new MemoryStream(data);
        using LZ4Stream lz4 = new LZ4Stream(input, CompressionMode.Decompress, true);
        using MemoryStream output = new MemoryStream();
        if (!small)
        {
            lz4.CopyTo(output);
            return output.ToArray();
        }

        byte[] buffer = new byte[4096];
        for (int i = 0;; i++)
        {
            int read = lz4.Read(buffer, 0, s_chunkSizes[i % s_chunkSizes.Length]);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>Decompresses a (corrupt) file on a worker; a decoder that spins past the deadline exits the process.</summary>
    private static int Fail(string file, int timeoutSeconds)
    {
        byte[] data = File.ReadAllBytes(file);
        Task<string> attempt = Task.Run(() =>
        {
            string read, dispose = "ok";
            LZ4Stream lz4 = new LZ4Stream(new MemoryStream(data), CompressionMode.Decompress, true);
            try
            {
                using MemoryStream output = new MemoryStream();
                lz4.CopyTo(output);
                read = $"OK ({output.Length} bytes)";
            }
            catch (Exception e)
            {
                read = $"THROW {e.GetType().FullName}: {e.Message}";
            }

            try
            {
                lz4.Dispose();
            }
            catch (Exception e)
            {
                dispose = $"THROW {e.GetType().FullName}: {e.Message}";
            }

            return $"read={read} | dispose={dispose}";
        });

        if (!attempt.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            Console.WriteLine($"HANG (> {timeoutSeconds}s)");
            Environment.Exit(EXIT_HANG); // the spinning worker cannot be canceled
        }

        Console.WriteLine(attempt.Result);
        return 0;
    }
}
