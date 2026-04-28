using Xunit;

namespace HelmRepoLite.Tests;

public class CliParserTests
{
    [Fact]
    public void Defaults_when_no_args()
    {
        var (opts, exit, _) = CliParser.Parse([]);
        Assert.Null(exit);
        Assert.Equal(8080, opts.Port);
        Assert.Equal("./charts", opts.StorageDir);
        Assert.True(opts.AnonymousGet);
        Assert.False(opts.Debug);
    }

    [Fact]
    public void Parses_equals_form()
    {
        var (opts, _, _) = CliParser.Parse(["--port=9090", "--storage-dir=/tmp/x"]);
        Assert.Equal(9090, opts.Port);
        Assert.Equal("/tmp/x", opts.StorageDir);
    }

    [Fact]
    public void Parses_space_form()
    {
        var (opts, _, _) = CliParser.Parse(["--port", "9090", "--basic-auth-user", "admin", "--basic-auth-pass", "s3cr3t"]);
        Assert.Equal(9090, opts.Port);
        Assert.Equal("admin", opts.BasicAuthUser);
        Assert.Equal("s3cr3t", opts.BasicAuthPass);
    }

    [Fact]
    public void Parses_bool_flags()
    {
        var (opts, _, _) = CliParser.Parse(["--debug", "--allow-overwrite", "--disable-delete"]);
        Assert.True(opts.Debug);
        Assert.True(opts.AllowOverwrite);
        Assert.True(opts.DisableDelete);
    }

    [Fact]
    public void Help_returns_exit_zero()
    {
        var (_, exit, msg) = CliParser.Parse(["--help"]);
        Assert.Equal(0, exit);
        Assert.NotNull(msg);
        Assert.Contains("helmrepolite", msg!);
    }

    [Fact]
    public void Unknown_arg_returns_exit_two()
    {
        var (_, exit, _) = CliParser.Parse(["whatever"]);
        Assert.Equal(2, exit);
    }
}
