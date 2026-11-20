using WhatsApp.Core.Models;
using WhatsApp.Core.Network;
using WhatsApp.Core.Storage;

var phone = args[0];
using var db = new Database(phone);
var node = new P2PNode(db, new Contact { Phone = phone, Name = "Utilisateur " + phone });
node.PeerPresence += (p, on) => Console.WriteLine($"[{p}] {(on ? "en ligne" : "hors ligne")}");
node.MessageReceived += m => { Console.WriteLine($"{m.SenderPhone}: {m.Text}"); node.MarkChatRead(m.ChatPhone); };
node.StatusChanged += (id, s) => Console.WriteLine($"statut {id[..4]} -> {s}");
node.Start();
Console.WriteLine("Prêt. Tape : <numéro> <message>");
while (true)
{
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) break;
    var i = line.IndexOf(' ');
    if (i > 0) await node.SendTextAsync(line[..i], line[(i + 1)..]);
}