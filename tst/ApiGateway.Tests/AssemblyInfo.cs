// These tests share one gateway container and one mounted route file: RouteReloadTests rewrites
// the route table while other tests are issuing requests through it. Running them in parallel
// makes unrelated tests fail whenever a reload is in flight, so the suite is serialised.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
