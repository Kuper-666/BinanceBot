using System;
using BinanceBotWpf.Services;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public interface ITestService
    {
        string Name { get; }
    }

    public class TestServiceA : ITestService
    {
        public string Name => "A";
    }

    public class TestServiceB : ITestService
    {
        public string Name => "B";
    }

    public class ServiceProviderTests
    {
        [Fact]
        public void Register_ThenGet_ReturnsSameInstance ()
        {
            var sp = new ServiceRegistry ();
            var svc = new TestServiceA ();
            sp.Register<ITestService> (svc);

            var result = sp.Get<ITestService> ();
            Assert.Same (svc, result);
        }

        [Fact]
        public void Get_Unregistered_ThrowsInvalidOperation ()
        {
            var sp = new ServiceRegistry ();
            Assert.Throws<InvalidOperationException> (() => sp.Get<ITestService> ());
        }

        [Fact]
        public void TryGet_Unregistered_ReturnsFalse ()
        {
            var sp = new ServiceRegistry ();
            bool found = sp.TryGet<ITestService> (out var svc);

            Assert.False (found);
            Assert.Null (svc);
        }

        [Fact]
        public void TryGet_Registered_ReturnsTrue ()
        {
            var sp = new ServiceRegistry ();
            var svc = new TestServiceA ();
            sp.Register<ITestService> (svc);

            bool found = sp.TryGet<ITestService> (out var result);

            Assert.True (found);
            Assert.Same (svc, result);
        }

        [Fact]
        public void RegisterFactory_CreatesInstanceOnFirstGet ()
        {
            var sp = new ServiceRegistry ();
            int callCount = 0;
            sp.RegisterFactory<ITestService> (() =>
            {
                callCount++;
                return new TestServiceA ();
            });

            Assert.Equal (0, callCount);

            var result1 = sp.Get<ITestService> ();
            Assert.Equal (1, callCount);

            var result2 = sp.Get<ITestService> ();
            Assert.Equal (1, callCount);
            Assert.Same (result1, result2);
        }

        [Fact]
        public void Register_OverwritesPrevious ()
        {
            var sp = new ServiceRegistry ();
            var svc1 = new TestServiceA ();
            var svc2 = new TestServiceB ();
            sp.Register<ITestService> (svc1);
            sp.Register<ITestService> (svc2);

            var result = sp.Get<ITestService> ();
            Assert.Same (svc2, result);
        }
    }
}
