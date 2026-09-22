using Xunit;

// Every test in this assembly owns its own PostgreSQL database, but all of them are created on, and dropped
// from, a single shared container through one administrative connection. Serialising the assembly keeps that
// container's database catalog under one writer at a time, which is what makes "CREATE DATABASE ... TEMPLATE"
// usable: PostgreSQL refuses to copy a template that any other session is connected to. It also keeps the
// container's connection budget bounded by one test rather than by the whole class list.
//
// This setting is scoped to this assembly. SmartProperty.UnitTests and SmartProperty.Api.IntegrationTests keep
// xUnit's default parallelism; neither of them touches a database.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
