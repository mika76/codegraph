using CodeGraph.Parsers;

namespace CodeGraph.Tests.Parsers;

public class WixParserTests
{
    [Fact]
    public void Installer_Features_Ships_Services()
    {
        const string wxs = """
            <?xml version="1.0"?>
            <Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
              <!-- <File Source="ignored.dll" /> -->
              <Product Id="*" Name="Acme Server $(var.Ver) -" Version="1.2.3" Manufacturer="Acme" UpgradeCode="G1">
                <Feature Id="MainFeature" Title="Main">
                  <ComponentGroupRef Id="Binaries" />
                </Feature>
                <ServiceInstall Name="AcmeSvc" Type="ownProcess" />
                <CustomAction Id="CA1" BinaryKey="B" DllEntry="Go" />
                <Shortcut Id="s" Name="Acme Link" />
              </Product>
              <Fragment>
                <ComponentGroup Id="Binaries">
                  <Component Id="c1"><File Source="$(var.Billing.TargetPath)" /></Component>
                  <Component Id="c2"><File Source="..\bin\Release\foo.dll" /></Component>
                </ComponentGroup>
              </Fragment>
            </Wix>
            """;
        var f = WixParser.Parse("Package/a.wxs", wxs);
        var inst = f.Symbols[0];
        Assert.Equal("installer", inst.Kind);
        Assert.Equal("Acme Server", inst.Name);
        Assert.Equal("1.2.3", inst.X["version"]!.GetValue<string>());
        Assert.Equal("G1", inst.X["upgrade_code"]!.GetValue<string>());
        Assert.Contains(f.Symbols, s => s.Kind == "feature" && s.Name == "MainFeature" && s.Line == 5);
        Assert.Contains(f.Symbols, s => s.Kind == "component_group" && s.Name == "Binaries");
        var wref = Assert.Single(f.Refs, r => r.Kind == "wix_ref");
        Assert.Equal("MainFeature", wref.FromParent);
        Assert.Equal("component_group", wref.X["ref_kind"]!.GetValue<string>());
        var ships = f.Refs.Where(r => r.Kind == "ships").ToList();
        Assert.DoesNotContain(ships, r => r.Name == "ignored.dll");
        Assert.Contains(ships, r => r.Name == "foo.dll" && r.FromParent == "Binaries" && r.X["ship_kind"]!.GetValue<string>() == "file");
        Assert.Contains(ships, r => r.Name == "" && r.X["project"]!.GetValue<string>() == "Billing" && r.X["ship_kind"]!.GetValue<string>() == "project output");
        Assert.Equal("AcmeSvc", f.X["services"]![0]!.GetValue<string>());
        Assert.Equal("CA1", f.X["custom_actions"]![0]!["Id"]!.GetValue<string>());
        Assert.Equal("Acme Link", f.X["shortcuts"]![0]!.GetValue<string>());
    }
}
