namespace ModIO
{
    public interface IImageLocator
    {
        string GetFileName();
        string GetURL();
    }

    public interface IMultiSizeImageLocator<E> : IImageLocator
    {
        string GetSizeURL(E size);
        SizeURLPair<E>[] GetAllURLs();
    }

    public struct SizeURLPair<E>
    {
        public E size;
        public string url;
    }
}
