{
  description = "Factorio Mod Studio: a local workbench for Factorio mod authors";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-25.11";

  outputs = { self, nixpkgs }:
    let
      lib = nixpkgs.lib;
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = f: lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});

      # The version lives in Directory.Build.props, like for the other builds.
      versionLine = lib.findFirst (lib.hasInfix "<Version>") null
        (lib.splitString "\n" (builtins.readFile ./Directory.Build.props));
      version = builtins.head (builtins.match ".*<Version>([^<]+)</Version>.*" versionLine);
    in
    {
      packages = forAllSystems (pkgs: rec {
        fmstudio = pkgs.buildDotnetModule {
          pname = "fmstudio";
          inherit version;
          src = lib.fileset.toSource {
            root = ./.;
            fileset = lib.fileset.unions [ ./src ./Directory.Build.props ./README.md ./LICENSE ];
          };
          projectFile = "src/FactorioModStudio.Cli/FactorioModStudio.Cli.csproj";
          # No NuGet packages: the projects use only .NET and ASP.NET Core.
          nugetDeps = ./nix/deps.json;
          dotnet-sdk = pkgs.dotnetCorePackages.sdk_8_0;
          dotnet-runtime = pkgs.dotnetCorePackages.aspnetcore_8_0;
          dotnetFlags = [ "-p:PackAsTool=false" ];
          executables = [ "fmstudio" ];

          meta = {
            description = "A local workbench for Factorio mod authors: validate, translate, pack and test-run mods";
            homepage = "https://github.com/4a11/factorio-mod-studio";
            license = lib.licenses.mit;
            mainProgram = "fmstudio";
            platforms = systems;
          };
        };
        default = fmstudio;
      });
    };
}
