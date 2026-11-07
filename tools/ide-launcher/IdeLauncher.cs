using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace CodeArenaIde;

/// <summary>
/// code-arena-ide.exe: Code Arena's IDE for a double-click, the desktop's shortcut or the Start menu on Windows. A program
/// of Windows' GUI subsystem, so Windows opens no console for it (code-arena.exe is a console program, for terminals, and
/// one started on its own flashes a console before it hides it: src/CodeArena/DesktopStart.cs). It starts code-arena.exe
/// beside it with the same arguments and folder, with a console that has no window (CREATE_NO_WINDOW) and the mark of a
/// start without one (CODE_ARENA_DETACHED), and ends; a message box says it when code-arena.exe cannot be started.
/// Written here as a .NET Framework 4 program (IL only, any CPU), which every Windows 10 and 11 runs as it is: a few
/// kilobytes, where a self-contained .NET program would be tens of megabytes. Made with System.Reflection.Metadata, so
/// building it needs nothing but the SDK; the same bytes every time (tools/publish-code-arena.sh).
/// </summary>
public static class IdeLauncher
{
    public const string FileName = "code-arena-ide.exe";
    public const string Program = "code-arena.exe";
    public const string Detached = "CODE_ARENA_DETACHED";
    public const string Failed = "Code Arena could not start code-arena.exe, which belongs beside code-arena-ide.exe: ";
    public const string FailedHow = "\n\nInstall Code Arena again, or run code-arena.exe in a terminal to see why.";

    // .NET Framework's own assemblies, by the key every one of them is signed with.
    private static readonly byte[] FrameworkKey = [0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89];

    public static void Write(string path, Version version) => File.WriteAllBytes(path, Build(version));

    public static byte[] Build(Version version)
    {
        var md = new MetadataBuilder();
        var mvid = md.ReserveGuid();
        md.AddModule(0, md.GetOrAddString(FileName), mvid.Handle, default, default);
        md.AddAssembly(md.GetOrAddString("code-arena-ide"), version, default, default, 0, AssemblyHashAlgorithm.Sha1);

        var framework = new Version(4, 0, 0, 0);
        var token = md.GetOrAddBlob(FrameworkKey);
        var mscorlib = md.AddAssemblyReference(md.GetOrAddString("mscorlib"), framework, default, token, 0, default);
        var system = md.AddAssemblyReference(md.GetOrAddString("System"), framework, default, token, 0, default);
        TypeReferenceHandle Type(AssemblyReferenceHandle from, string ns, string name) => md.AddTypeReference(from, md.GetOrAddString(ns), md.GetOrAddString(name));
        var objectType = Type(mscorlib, "System", "Object");
        var stringType = Type(mscorlib, "System", "String");
        var environment = Type(mscorlib, "System", "Environment");
        var appDomain = Type(mscorlib, "System", "AppDomain");
        var pathType = Type(mscorlib, "System.IO", "Path");
        var exception = Type(mscorlib, "System", "Exception");
        var startInfo = Type(system, "System.Diagnostics", "ProcessStartInfo");
        var process = Type(system, "System.Diagnostics", "Process");
        var dictionary = Type(system, "System.Collections.Specialized", "StringDictionary");

        // A member of a type of the framework's, by its signature: static or not, what it returns, its parameters.
        MemberReferenceHandle Member(TypeReferenceHandle type, string name, bool instance, Action<ReturnTypeEncoder> returns, params Action<SignatureTypeEncoder>[] parameters)
        {
            var sig = new BlobBuilder();
            new BlobEncoder(sig).MethodSignature(isInstanceMethod: instance).Parameters(parameters.Length, returns, ps =>
            {
                foreach (var p in parameters)
                {
                    p(ps.AddParameter().Type());
                }
            });
            return md.AddMemberReference(type, md.GetOrAddString(name), md.GetOrAddBlob(sig));
        }
        static void Void(ReturnTypeEncoder r) => r.Void();
        static void Text(ReturnTypeEncoder r) => r.Type().String();
        static void Int(ReturnTypeEncoder r) => r.Type().Int32();
        Action<ReturnTypeEncoder> Of(TypeReferenceHandle t) => r => r.Type().Type(t, isValueType: false);
        static void S(SignatureTypeEncoder t) => t.String();
        static void I(SignatureTypeEncoder t) => t.Int32();
        static void C(SignatureTypeEncoder t) => t.Char();
        static void B(SignatureTypeEncoder t) => t.Boolean();

        var commandLine = Member(environment, "get_CommandLine", false, Text);
        var currentDirectory = Member(environment, "get_CurrentDirectory", false, Text);
        var length = Member(stringType, "get_Length", true, Int);
        var charAt = Member(stringType, "get_Chars", true, r => r.Type().Char(), I);
        var indexOfFrom = Member(stringType, "IndexOf", true, Int, C, I);
        var indexOf = Member(stringType, "IndexOf", true, Int, C);
        var substring = Member(stringType, "Substring", true, Text, I);
        var trim = Member(stringType, "Trim", true, Text);
        var concat = Member(stringType, "Concat", false, Text, S, S, S);
        var currentDomain = Member(appDomain, "get_CurrentDomain", false, Of(appDomain));
        var baseDirectory = Member(appDomain, "get_BaseDirectory", true, Text);
        var combine = Member(pathType, "Combine", false, Text, S, S);
        var newStartInfo = Member(startInfo, ".ctor", true, Void, S);
        var setArguments = Member(startInfo, "set_Arguments", true, Void, S);
        var setShellExecute = Member(startInfo, "set_UseShellExecute", true, Void, B);
        var setNoWindow = Member(startInfo, "set_CreateNoWindow", true, Void, B);
        var setFolder = Member(startInfo, "set_WorkingDirectory", true, Void, S);
        var variables = Member(startInfo, "get_EnvironmentVariables", true, Of(dictionary));
        var setItem = Member(dictionary, "set_Item", true, Void, S, S);
        var start = Member(process, "Start", false, Of(process), t => t.Type(startInfo, isValueType: false));
        var message = Member(exception, "get_Message", true, Text);

        var il = new BlobBuilder();
        var bodies = new MethodBodyStreamEncoder(il);
        var methods = MetadataTokens.MethodDefinitionHandle(1);
        var alert = MetadataTokens.MethodDefinitionHandle(2);
        var messageBox = MetadataTokens.MethodDefinitionHandle(3);

        // static int Main(): the command line less the program's own name (quoted or not), passed on as it was typed.
        var locals = new BlobBuilder();
        var l = new BlobEncoder(locals).LocalVariableSignature(3);
        l.AddVariable().Type().String();
        l.AddVariable().Type().Int32();
        l.AddVariable().Type().Type(startInfo, isValueType: false);
        var flow = new ControlFlowBuilder();
        var e = new InstructionEncoder(new BlobBuilder(), flow);
        var (unquoted, found, after, tryStart, tryEnd, handlerEnd, done, failed) =
            (e.DefineLabel(), e.DefineLabel(), e.DefineLabel(), e.DefineLabel(), e.DefineLabel(), e.DefineLabel(), e.DefineLabel(), e.DefineLabel());
        void Virt(MemberReferenceHandle m)
        {
            e.OpCode(ILOpCode.Callvirt);
            e.Token(m);
        }
        e.Call(commandLine);
        e.StoreLocal(0);
        e.LoadLocal(0);
        Virt(length);
        e.Branch(ILOpCode.Brfalse, unquoted);
        e.LoadLocal(0);
        e.LoadConstantI4(0);
        Virt(charAt);
        e.LoadConstantI4('"');
        e.Branch(ILOpCode.Bne_un, unquoted);
        e.LoadLocal(0);
        e.LoadConstantI4('"');
        e.LoadConstantI4(1);
        Virt(indexOfFrom);
        e.LoadConstantI4(1);
        e.OpCode(ILOpCode.Add);
        e.StoreLocal(1);
        e.Branch(ILOpCode.Br, found);
        e.MarkLabel(unquoted);
        e.LoadLocal(0);
        e.LoadConstantI4(' ');
        Virt(indexOf);
        e.StoreLocal(1);
        e.MarkLabel(found);
        // No space, or no closing quote: there are no arguments.
        e.LoadLocal(1);
        e.LoadConstantI4(0);
        e.Branch(ILOpCode.Bgt, after);
        e.LoadLocal(0);
        Virt(length);
        e.StoreLocal(1);
        e.MarkLabel(after);
        e.Call(currentDomain);
        Virt(baseDirectory);
        e.LoadString(md.GetOrAddUserString(Program));
        e.Call(combine);
        e.OpCode(ILOpCode.Newobj);
        e.Token(newStartInfo);
        e.StoreLocal(2);
        e.LoadLocal(2);
        e.LoadLocal(0);
        e.LoadLocal(1);
        Virt(substring);
        Virt(trim);
        Virt(setArguments);
        e.LoadLocal(2);
        e.LoadConstantI4(0);
        Virt(setShellExecute);
        e.LoadLocal(2);
        e.LoadConstantI4(1);
        Virt(setNoWindow);
        e.LoadLocal(2);
        e.Call(currentDirectory);
        Virt(setFolder);
        e.LoadLocal(2);
        Virt(variables);
        e.LoadString(md.GetOrAddUserString(Detached));
        e.LoadString(md.GetOrAddUserString("1"));
        Virt(setItem);
        e.MarkLabel(tryStart);
        e.LoadLocal(2);
        e.Call(start);
        e.OpCode(ILOpCode.Pop);
        e.Branch(ILOpCode.Leave, done);
        e.MarkLabel(tryEnd);
        Virt(message);
        e.Call(alert);
        e.Branch(ILOpCode.Leave, failed);
        e.MarkLabel(handlerEnd);
        e.MarkLabel(done);
        e.LoadConstantI4(0);
        e.OpCode(ILOpCode.Ret);
        e.MarkLabel(failed);
        e.LoadConstantI4(1);
        e.OpCode(ILOpCode.Ret);
        flow.AddCatchRegion(tryStart, tryEnd, tryEnd, handlerEnd, exception);
        var mainBody = bodies.AddMethodBody(e, maxStack: 4, localVariablesSignature: md.AddStandaloneSignature(md.GetOrAddBlob(locals)));

        // static void Alert(string why): a message box, as there is no console to say it in.
        var a = new InstructionEncoder(new BlobBuilder());
        a.LoadConstantI4(0);
        a.OpCode(ILOpCode.Conv_i);
        a.LoadString(md.GetOrAddUserString(Failed));
        a.LoadArgument(0);
        a.LoadString(md.GetOrAddUserString(FailedHow));
        a.Call(concat);
        a.LoadString(md.GetOrAddUserString("Code Arena"));
        a.LoadConstantI4(0x10); // MB_ICONERROR
        a.Call(messageBox);
        a.OpCode(ILOpCode.Pop);
        a.OpCode(ILOpCode.Ret);
        var alertBody = bodies.AddMethodBody(a);

        BlobHandle Signature(Action<ReturnTypeEncoder> returns, params Action<SignatureTypeEncoder>[] parameters)
        {
            var sig = new BlobBuilder();
            new BlobEncoder(sig).MethodSignature().Parameters(parameters.Length, returns, ps =>
            {
                foreach (var p in parameters)
                {
                    p(ps.AddParameter().Type());
                }
            });
            return md.GetOrAddBlob(sig);
        }

        md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), methods);
        md.AddTypeDefinition(TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            md.GetOrAddString("CodeArena"), md.GetOrAddString("IdeLauncher"), objectType, MetadataTokens.FieldDefinitionHandle(1), methods);
        var none = MetadataTokens.ParameterHandle(1);
        md.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, MethodImplAttributes.IL,
            md.GetOrAddString("Main"), Signature(Int), mainBody, none);
        md.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, MethodImplAttributes.IL,
            md.GetOrAddString("Alert"), Signature(Void, S), alertBody, none);
        md.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.PinvokeImpl,
            MethodImplAttributes.PreserveSig, md.GetOrAddString("MessageBoxW"), Signature(Int, t => t.IntPtr(), S, S, t => t.UInt32()), -1, none);
        md.AddMethodImport(messageBox, MethodImportAttributes.CharSetUnicode | MethodImportAttributes.ExactSpelling | MethodImportAttributes.CallingConventionWinApi,
            md.GetOrAddString("MessageBoxW"), md.AddModuleReference(md.GetOrAddString("user32.dll")));

        var header = new PEHeaderBuilder(machine: Machine.I386, imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware | Characteristics.Bit32Machine,
            subsystem: Subsystem.WindowsGui, dllCharacteristics: DllCharacteristics.DynamicBase | DllCharacteristics.NxCompatible | DllCharacteristics.NoSeh
                | DllCharacteristics.TerminalServerAware);
        var pe = new ManagedPEBuilder(header, new MetadataRootBuilder(md), il, entryPoint: methods, flags: CorFlags.ILOnly,
            deterministicIdProvider: content => BlobContentId.FromHash(System.Security.Cryptography.SHA256.HashData([.. content.SelectMany(b => b.GetBytes())])[..20]));
        var image = new BlobBuilder();
        var id = pe.Serialize(image);
        new BlobWriter(mvid.Content).WriteGuid(id.Guid);
        return image.ToArray();
    }
}
