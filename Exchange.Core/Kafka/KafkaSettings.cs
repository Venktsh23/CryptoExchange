namespace Exchange.Core.Kafka;

public class KafkaSettings
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string TradesTopic { get; set; } = "trades";
    public string ConsumerGroupId { get; set; } = "settlement-worker";
}