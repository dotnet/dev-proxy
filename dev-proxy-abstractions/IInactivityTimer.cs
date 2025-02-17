namespace DevProxy.Abstractions
{
    public interface IInactivityTimer
    {
        void Reset();
        void Stop();
    }
}