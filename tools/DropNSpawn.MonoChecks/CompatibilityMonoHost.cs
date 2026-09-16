// Runs only in the disposable child launched by Run.ps1.
using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class CompatibilityMonoHost
{
    private const string MonoDll = "mono-2.0-bdwgc.dll";
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetDllDirectory(string path);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern void mono_set_dirs(string assemblies, string config);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern void mono_set_assemblies_path(string path);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern void mono_config_parse(string path);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_jit_init_version(string name, string version);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern void mono_domain_set_config(IntPtr domain, string baseDir, string configFileName);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_domain_assembly_open(IntPtr domain, string file);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_assembly_get_image(IntPtr assembly);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_class_from_name(IntPtr image, string namesp, string name);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_class_get_method_from_name(IntPtr klass, string name, int count);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_runtime_invoke(IntPtr method, IntPtr instance, IntPtr arguments, out IntPtr exception);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_object_unbox(IntPtr value);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern void mono_jit_cleanup(IntPtr domain);
    [DllImport(MonoDll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_get_runtime_build_info();

    private static int Main(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        if (args.Length != 5) return 2;
        IntPtr domain = IntPtr.Zero;
        try
        {
            if (IntPtr.Size != 8 || !SetDllDirectory(args[0])) throw new InvalidOperationException("A 64-bit helper and valid runtime path are required.");
            // Process-local settings only. The game installation is never changed.
            Environment.SetEnvironmentVariable("DROPNSPAWN_MONO_PROBE_DLL", args[4]);
            Environment.SetEnvironmentVariable("DROPNSPAWN_MONO_PROBE_MANAGED", args[1]);
            mono_set_dirs(args[1], args[2]);
            mono_set_assemblies_path(args[1]);
            mono_config_parse(Path.Combine(args[2], "mono", "config"));
            Console.WriteLine("Mono native build: " + Marshal.PtrToStringAnsi(mono_get_runtime_build_info()));
            domain = mono_jit_init_version("DropNSpawn.CompatibilityProbe", "v4.0.30319");
            if (domain == IntPtr.Zero) throw new InvalidOperationException("Mono domain initialization failed.");
            mono_domain_set_config(domain, Path.GetDirectoryName(args[3]), "CompatibilityProbe.exe.config");
            IntPtr assembly = mono_domain_assembly_open(domain, args[3]);
            if (assembly == IntPtr.Zero) throw new InvalidOperationException("Mono could not load the probe.");
            IntPtr klass = mono_class_from_name(mono_assembly_get_image(assembly), "DropNSpawn.Tests", "CompatibilityProbe");
            if (klass == IntPtr.Zero) throw new InvalidOperationException("Mono probe class was not found.");
            IntPtr method = mono_class_get_method_from_name(klass, "Run", 0);
            if (method == IntPtr.Zero) throw new InvalidOperationException("Mono probe entry point was not found.");
            IntPtr exception;
            IntPtr result = mono_runtime_invoke(method, IntPtr.Zero, IntPtr.Zero, out exception);
            if (exception != IntPtr.Zero) throw new InvalidOperationException("The managed Mono probe raised an uncaught exception.");
            return result == IntPtr.Zero ? 3 : Marshal.ReadInt32(mono_object_unbox(result));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Mono host failed: " + error.GetType().Name + ": " + error.Message);
            return 1;
        }
        finally
        {
            if (domain != IntPtr.Zero) mono_jit_cleanup(domain);
        }
    }
}
