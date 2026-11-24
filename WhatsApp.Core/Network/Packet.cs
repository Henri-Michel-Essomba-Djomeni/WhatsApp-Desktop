using WhatsApp.Core.Models;

namespace WhatsApp.Core.Network;

public enum PacketType { Hello, Message, Delivered, Read, Typing, Delete, Status, StatusDelete,
                         CallInvite, CallAccept, CallReject, CallEnd }
// Un seul format de paquet pour tous les échanges (sérialisé en JSON).
public class Packet
{
    public PacketType Type { get; set; }
    public string From { get; set; } = "";
    // Hello : profil de l'expéditeur (Data = photo de profil)
    public string Name { get; set; } = "";
    public string About { get; set; } = "";
    // Message / Delivered / Delete
    public string Id { get; set; } = "";
    public MessageType MsgType { get; set; }
    public string Text { get; set; } = "";
    public string? ReplyToId { get; set; }
    public long SentAtMs { get; set; }
    public string FileName { get; set; } = "";
    public byte[]? Data { get; set; }            // image, vocal, fichier...
    // Read : liste des messages lus
    public List<string> Ids { get; set; } = new();
    // Typing
    public bool IsTyping { get; set; }
    public string Color { get; set; } = "";   // fond d'un statut texte
    public int Port { get; set; }       // port UDP audio de l'appelant ou du destinataire
    public bool Video { get; set; }     // réservé à l'appel vidéo
    public int VideoPort { get; set; }   // port UDP des images vidéo
}