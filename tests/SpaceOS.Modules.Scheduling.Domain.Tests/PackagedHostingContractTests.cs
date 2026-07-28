using System;
using System.IO;
using System.Linq;
using System.Reflection;
using SpaceOS.Modules.Hosting.RlsFixtures;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Proves the ERPSEP-05 packaging chain: this module lives in its OWN repository and can
/// still reach the shared hosting contract — as a versioned package, never through a
/// relative ProjectReference into the platform repo.
/// </summary>
/// <remarks>
/// <para>
/// This is a compile-and-load assertion, deliberately Docker-free so it runs on every CI
/// build. The real RLS proof (four facts against a live PostgreSQL) arrives with the M2
/// persistence layer; until the schema exists there is nothing to assert isolation on.
/// </para>
/// <para>
/// If this test ever fails, the cause is almost certainly packaging, not scheduling logic:
/// a missing feed credential, an unpublished version, or a package source mapping that no
/// longer routes SpaceOS.* to the private feed.
/// </para>
/// </remarks>
public sealed class PackagedHostingContractTests
{
    [Fact]
    public void The_shared_rls_fixture_is_loadable_here()
    {
        var assembly = typeof(NonSuperuserRlsFixture).Assembly;

        Assert.Equal("SpaceOS.Modules.Hosting.RlsFixtures", assembly.GetName().Name);
    }

    [Fact]
    public void No_project_file_reaches_into_the_platform_repository()
    {
        // The runtime cannot tell a package from a project reference (both are copied to the
        // output directory), so the rule is enforced where it actually lives: in the build
        // graph. This is the ADR-067 / ERPSEP-05 invariant in executable form.
        var repositoryRoot = FindRepositoryRoot();
        var projectFiles = Directory.GetFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories);

        Assert.NotEmpty(projectFiles);

        var offenders = projectFiles
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(project =>
                project.Text.Contains("spaceos-modules-hosting", StringComparison.OrdinalIgnoreCase) ||
                project.Text.Contains("joinerytech-platform", StringComparison.OrdinalIgnoreCase))
            .Select(project => Path.GetRelativePath(repositoryRoot, project.Path))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These project files reach into the platform repository instead of consuming a " +
            $"published package: {string.Join(", ", offenders)}");

        var declaresPackage = projectFiles.Any(path =>
            File.ReadAllText(path).Contains(
                "PackageReference Include=\"SpaceOS.Modules.Hosting.RlsFixtures\"",
                StringComparison.Ordinal));

        Assert.True(declaresPackage, "The shared hosting contract must arrive as a PackageReference.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.GetFiles("*.sln").Length == 0)
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    [Fact]
    public void The_package_exposes_the_members_the_planned_M2_proof_needs()
    {
        var type = typeof(NonSuperuserRlsFixture);

        // These four are exactly what the audit's §5.2 proof plan calls for: a non-superuser
        // app role, its catalogue properties, FORCE RLS per table, and the tenant GUC.
        Assert.NotNull(type.GetMethod(nameof(NonSuperuserRlsFixture.CreateApplicationRoleAsync)));
        Assert.NotNull(type.GetMethod(nameof(NonSuperuserRlsFixture.ReadApplicationRolePropertiesAsync)));
        Assert.NotNull(type.GetMethod(nameof(NonSuperuserRlsFixture.ReadForceRlsCatalogAsync)));
        Assert.NotNull(type.GetMethod(nameof(NonSuperuserRlsFixture.SetTenantAsync)));
    }

    [Fact]
    public void The_catalogue_state_record_carries_both_rls_flags()
    {
        // ENABLE alone is not enough: without FORCE, the table owner bypasses the policy.
        var properties = typeof(CatalogRlsState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(CatalogRlsState.RelRowSecurity), properties);
        Assert.Contains(nameof(CatalogRlsState.RelForceRowSecurity), properties);
    }
}
