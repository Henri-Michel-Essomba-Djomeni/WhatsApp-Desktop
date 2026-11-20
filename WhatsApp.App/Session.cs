using WhatsApp.Core.Models;
using WhatsApp.Core.Network;
using WhatsApp.Core.Storage;

namespace WhatsApp.App;

public static class Session
{
    public static Database Db = null!;
    public static P2PNode Node = null!;
    public static Contact Me = null!;

    public static void Start(string phone, string name)
    {
        Db = new Database(phone);
        Db.SetSetting("name", name);
        Me = new Contact
        {
            Phone = phone, Name = name,
            About = Db.GetSetting("about") ?? "Hey, j'utilise WhatsApp",
            AvatarPath = Db.GetSetting("avatar")
        };
        Node = new P2PNode(Db, Me);
        Node.Start();
    }

    public static void Stop() { Node?.Dispose(); Db?.Dispose(); }
}