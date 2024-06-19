namespace Quaver.Steam.Deploy;

public class GameBuild
{
    internal string Name { get; set; }
    internal string QuaverSharedMd5 { get; set; }
    
    internal string QuaverApiMd5 { get; set; }
    
    internal string QuaverServerCommonMd5 { get; set; }
    
    internal string QuaverServerClientMd5 { get; set; }

    public GameBuild() {}
    public GameBuild(string name, string quaverSharedMd5, string quaverApiMd5, string quaverServerCommonMd5, string quaverServerClientMd5)
    {
        Name = name;
        QuaverSharedMd5 = quaverSharedMd5;
        QuaverApiMd5 = quaverApiMd5;
        QuaverServerCommonMd5 = quaverServerCommonMd5;
        QuaverServerClientMd5 = quaverServerClientMd5;
    }
    
    public override string ToString()
    {
        return $"Quaver.Shared {QuaverSharedMd5} " +
               $"Quaver.API {QuaverApiMd5} " +
               $"Quaver.Server.Common {QuaverServerCommonMd5} " +
               $"Quaver.Server.Client {QuaverServerClientMd5} ";
    }
}