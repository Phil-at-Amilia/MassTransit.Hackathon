namespace MassTransit.Hackathon.Messages;

/// <summary>
/// Message contract for a kitchen order that travels across the bus.
/// MassTransit uses this interface to create a fanout exchange in RabbitMQ
/// named after the full type name.
/// </summary>
public interface IOrderMessage
{
    /// <summary>The item being ordered.</summary>
    OrderItem Item { get; }

    /// <summary>UTC timestamp set by the waiter.</summary>
    DateTime PlacedAt { get; }
}
