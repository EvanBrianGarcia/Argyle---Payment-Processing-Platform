// Static env vars set by PaymentApiFactory and MessagingApiFactory are
// process-wide. Running test classes in parallel races on those vars,
// which has caused migration-conflict and missed-delivery flakes when
// the wrong connection string or RabbitMQ host leaks across classes.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
