using CodeGraph.Parsers;

namespace CodeGraph.Tests.Parsers;

public class ProjectParserTests
{
    [Fact]
    public void Vbp()
    {
        const string vbp = "Type=OleDll\nReference=*\\G{00020430}#2.0#0#..\\stdole2.tlb#OLE Automation\nReference=*\\A..\\Shared\\Lib.vbp\n" +
            "Object={831FDD16}#2.0#0; MSCOMCTL.OCX\nForm=frmMain.frm\nModule=modGen; ..\\Shared\\modGen.bas\nClass=clsA; clsA.cls\n" +
            "Name=\"Orders\"\nMajorVer=1\nMinorVer=2\nRevisionVer=3\nVersionComments=\"hi\"\n";
        var p = ProjectParser.Parse("x/Orders.vbp", vbp).Project!;
        Assert.Equal("vbp", p.Format);
        Assert.Equal("vb6", p.Lang);
        Assert.Equal("Orders", p.Name);
        Assert.Equal("dll", p.Type);
        Assert.Equal("Orders.dll", p.Output);
        Assert.Equal("1.2.3", p.Version);
        Assert.Equal(["frmMain.frm", "..\\Shared\\modGen.bas", "clsA.cls"], p.Members);
        Assert.Equal(["..\\Shared\\Lib.vbp"], p.ProjectRefs);
        Assert.Contains(p.Libs, l => l.Kind == "com" && l.File == "stdole2.tlb" && l.Name == "OLE Automation");
        Assert.Contains(p.Libs, l => l.Kind == "ocx" && l.File == "MSCOMCTL.OCX");
        Assert.Equal("hi", p.X["version_comments"]!.GetValue<string>());
    }

    [Fact]
    public void Solutions()
    {
        var sln = ProjectParser.Parse("a.sln", "Project(\"{FAE04EC0}\") = \"A\", \"src\\A\\A.csproj\", \"{1}\"\nEndProject\n" +
            "Project(\"{2150E333}\") = \"Folder\", \"Folder\", \"{2}\"\nEndProject\n").Solution!;
        Assert.Equal(["src\\A\\A.csproj"], sln.Projects);
        var vbg = ProjectParser.Parse("g.vbg", "VBGROUP 5.0\nStartupProject=A\\A.vbp\nProject=B\\B.vbp\n").Solution!;
        Assert.Equal(["A\\A.vbp", "B\\B.vbp"], vbg.Projects);
        var slnx = ProjectParser.Parse("a.slnx", "<Solution><Project Path=\"x/X.csproj\"/><Folder Name=\"/f/\"><Project Path=\"y/Y.vcxproj\"/></Folder></Solution>").Solution!;
        Assert.Equal("slnx", slnx.Format);
        Assert.Equal(["x/X.csproj", "y/Y.vcxproj"], slnx.Projects);
    }

    [Fact]
    public void Csproj_SdkAndClassic()
    {
        const string sdk = """
            <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework><Version>2.0.1</Version></PropertyGroup>
            <ItemGroup><PackageReference Include="Dapper" Version="2.1" /><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup></Project>
            """;
        var p = ProjectParser.Parse("Api/Api.csproj", sdk).Project!;
        Assert.True(p.SdkStyle);
        Assert.Equal("exe", p.Type);
        Assert.Equal("Api.exe", p.Output);
        Assert.Equal("2.0.1", p.Version);
        Assert.Equal("net8.0", p.X["framework"]!.GetValue<string>());
        Assert.Contains(p.Libs, l => l.Kind == "package" && l.Name == "Dapper" && l.Version == "2.1");
        Assert.Equal(["..\\Lib\\Lib.csproj"], p.ProjectRefs);

        const string classic = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><OutputType>WinExe</OutputType><AssemblyName>Svc</AssemblyName><RootNamespace>Acme.Svc</RootNamespace><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup>
              <ItemGroup><Compile Include="A.cs" /><Content Include="x.png" /><None Include="run.ps1" />
                <Reference Include="System.Data, Version=4.0.0.0"><HintPath>..\lib\Sys.dll</HintPath></Reference></ItemGroup>
            </Project>
            """;
        var c = ProjectParser.Parse("Svc.csproj", classic).Project!;
        Assert.False(c.SdkStyle);
        Assert.Equal("Svc", c.Name);
        Assert.Equal("Svc.exe", c.Output);
        Assert.Equal(["A.cs", "run.ps1"], c.Members);
        Assert.Equal("Acme.Svc", c.X["root_namespace"]!.GetValue<string>());
        var lib = Assert.Single(c.Libs);
        Assert.Equal("System.Data", lib.Name);
        Assert.Equal("..\\lib\\Sys.dll", lib.Path);
    }

    [Fact]
    public void NativeAndWix()
    {
        const string vcx = """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><ConfigurationType>DynamicLibrary</ConfigurationType><RootNamespace>RandLib</RootNamespace></PropertyGroup>
            <ItemGroup><ClCompile Include="a.cpp" /><ClInclude Include="a.h" /></ItemGroup>
            <ItemDefinitionGroup><Link><AdditionalDependencies>kernel32.lib;%(AdditionalDependencies)</AdditionalDependencies></Link></ItemDefinitionGroup></Project>
            """;
        var v = ProjectParser.Parse("RandLib.vcxproj", vcx).Project!;
        Assert.Equal("RandLib", v.Name);
        Assert.Equal("dll", v.Type);
        Assert.Equal("RandLib.dll", v.Output);
        Assert.Equal(["a.cpp", "a.h"], v.Members);
        Assert.Equal("kernel32.lib", Assert.Single(v.Libs).Name);

        var dsp = ProjectParser.Parse("old.dsp", "# TARGTYPE \"Win32 (x86) Dynamic-Link Library\" 0x0102\n# Name \"Old - Win32 Release\"\nSOURCE=.\\a.cpp\n").Project!;
        Assert.Equal("Old", dsp.Name);
        Assert.Equal("Old.dll", dsp.Output);
        Assert.Equal([".\\a.cpp"], dsp.Members);

        var vcp = ProjectParser.Parse("o.vcproj", "<VisualStudioProject Name=\"Leg\"><Configurations><Configuration ConfigurationType=\"1\"/></Configurations><Files><File RelativePath=\".\\x.cpp\"/></Files></VisualStudioProject>").Project!;
        Assert.Equal("Leg.exe", vcp.Output);
        Assert.Equal([".\\x.cpp"], vcp.Members);

        var wix = ProjectParser.Parse("Setup.wixproj", "<Project Sdk=\"WixToolset.Sdk/4.0\"><PropertyGroup><OutputName>AcmeSetup</OutputName></PropertyGroup><ItemGroup><PackageReference Include=\"WixToolset.UI.wixext\" Version=\"4.0\" /></ItemGroup></Project>").Project!;
        Assert.Equal("installer", wix.Type);
        Assert.Equal("AcmeSetup.msi", wix.Output);
        Assert.True(wix.SdkStyle);

        var sql = ProjectParser.Parse("Db.sqlproj", "<Project><PropertyGroup><Name>AcmeDb</Name></PropertyGroup></Project>").Project!;
        Assert.Equal("AcmeDb", sql.Name);
        Assert.Equal("database", sql.Type);
        Assert.Equal("Db.dacpac", sql.Output);
    }

    [Fact]
    public void BrokenXml_ReportsError()
    {
        var p = ProjectParser.Parse("x.csproj", "<Project><oops></Project>").Project!;
        Assert.Equal("x", p.Name);
        Assert.NotNull(p.Error);
        Assert.Empty(p.Members);
    }

    [Fact]
    public void Dsp_output_comes_from_the_release_linker_out()
    {
        const string dsp = """
            # Microsoft Developer Studio Project File - Name="GridCtl" - Package Owner=<4>
            # TARGTYPE "Win32 (x86) Dynamic-Link Library" 0x0102
            !IF  "$(CFG)" == "GridCtl - Win32 Release"
            # ADD LINK32 kernel32.lib /nologo /subsystem:windows /dll /machine:I386 /out:"../../Controls/GridCtl.ocx"
            !ELSEIF  "$(CFG)" == "GridCtl - Win32 Debug"
            # ADD LINK32 kernel32.lib /nologo /dll /debug /out:"Debug/GridCtlD.ocx"
            !ENDIF
            # Name "GridCtl - Win32 Release"
            SOURCE=.\GridCtl.cpp
            """;
        var p = ProjectParser.Parse("Controls/GridCtl.dsp", dsp).Project!;
        Assert.Equal("GridCtl.ocx", p.Output);
        Assert.Equal("ocx", p.Type);
    }

    [Fact]
    public void Vcxproj_output_from_linker_or_target_ext()
    {
        const string vcx = """
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><ConfigurationType>DynamicLibrary</ConfigurationType></PropertyGroup>
              <PropertyGroup><TargetExt Condition="'$(Configuration)|$(Platform)'=='Release|Win32'">.ocx</TargetExt></PropertyGroup>
              <ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='Debug|Win32'">
                <Link><OutputFile>Debug/GridCtlD.ocx</OutputFile></Link>
                <Bscmake><OutputFile>.\Debug/GridCtl.bsc</OutputFile></Bscmake>
              </ItemDefinitionGroup>
              <ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='Release|Win32'">
                <Bscmake><OutputFile>.\Release/GridCtl.bsc</OutputFile></Bscmake>
                <Link><OutputFile>../../Controls/GridCtl.ocx</OutputFile></Link>
              </ItemDefinitionGroup>
            </Project>
            """;
        var p = ProjectParser.Parse("Controls/GridCtl.vcxproj", vcx).Project!;
        Assert.Equal("GridCtl.ocx", p.Output);
        Assert.Equal("ocx", p.Type);

        // linker output made of macros -> TargetName + TargetExt
        var macro = ProjectParser.Parse("Controls/GridCtl.vcxproj", vcx
            .Replace("../../Controls/GridCtl.ocx", "$(OutDir)$(TargetName)$(TargetExt)")
            .Replace("Debug/GridCtlD.ocx", "$(OutDir)$(TargetName)$(TargetExt)")).Project!;
        Assert.Equal("GridCtl.ocx", macro.Output);
    }

    [Fact]
    public void Vcproj_output_from_the_linker_tool_release_first()
    {
        const string vc = """
            <VisualStudioProject ProjectType="Visual C++" Name="GridCtl">
              <Configurations>
                <Configuration Name="Debug|Win32" ConfigurationType="2">
                  <Tool Name="VCLinkerTool" OutputFile="Debug/GridCtlD.ocx" />
                </Configuration>
                <Configuration Name="Release|Win32" ConfigurationType="2">
                  <Tool Name="VCBscMakeTool" OutputFile=".\Release/GridCtl.bsc" />
                  <Tool Name="VCLinkerTool" OutputFile="../../Controls/GridCtl.ocx" />
                </Configuration>
              </Configurations>
            </VisualStudioProject>
            """;
        var p = ProjectParser.Parse("Controls/GridCtl.vcproj", vc).Project!;
        Assert.Equal("GridCtl.ocx", p.Output);
        Assert.Equal("ocx", p.Type);
    }

    [Fact]
    public void Dsp_without_out_keeps_name_and_type_and_unquoted_out_works()
    {
        var plain = ProjectParser.Parse("old.dsp", "# TARGTYPE \"Win32 (x86) Dynamic-Link Library\" 0x0102\n# Name \"Old - Win32 Release\"\n").Project!;
        Assert.Equal("Old.dll", plain.Output);
        var lib = ProjectParser.Parse("util.dsp",
            "# TARGTYPE \"Win32 (x86) Static Library\" 0x0104\n# ADD LIB32 /nologo /out:..\\lib\\UtilLib.lib\n# Name \"Util - Win32 Release\"\n").Project!;
        Assert.Equal("UtilLib.lib", lib.Output);
        Assert.Equal("lib", lib.Type);
    }
}
