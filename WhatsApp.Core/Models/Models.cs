namespace WhatsApp.Core.Models;

public enum MessageType { Text = 0, Image = 1, File = 2, Voice = 3, Sticker = 4 }

// Coches : Pending = horloge, Sent = 1 coche, Delivered = 2 coches grises, Read = 2 coches bleues
public enum MessageStatus { Pending = 0, Sent = 1, Delivered = 2, Read = 3 }

public class Contact
{
    public string Phone { get; set; } = "";      // identité unique dans le réseau P2P
    public string Name { get; set; } = "";
    public string About { get; set; } = "";
    public string? AvatarPath { get; set; }      // chemin du fichier image, jamais l'image elle-même
}

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ChatPhone { get; set; } = "";   // l'interlocuteur de la conversation
    public string SenderPhone { get; set; } = "";
    public MessageType Type { get; set; } = MessageType.Text;
    public string Text { get; set; } = "";
    public string? MediaPath { get; set; }
    public DateTime SentAt { get; set; } = DateTime.Now;
    public MessageStatus Status { get; set; } = MessageStatus.Pending;
    public string? ReplyToId { get; set; }
    public bool IsDeleted { get; set; }
}

// Une ligne de la liste de discussions (barre de gauche)
public record ChatSummary(Contact Contact, ChatMessage? LastMessage, int Unread);