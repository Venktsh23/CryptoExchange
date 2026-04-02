namespace Exchange.Core.Kafka;

public class KafkaSettings
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string OrdersTopic      { get; set; } = "orders";
    public string TradesTopic      { get; set; } = "trades";

    // Each consumer group tracks its own offset independently
    // Engine reads orders — its own group
    // Settlement reads trades — its own group
    public string EngineConsumerGroup     { get; set; } = "matching-engine";
    public string SettlementConsumerGroup { get; set; } = "settlement-worker";
}