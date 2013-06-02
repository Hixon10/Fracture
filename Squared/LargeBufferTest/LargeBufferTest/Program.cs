using System;

namespace LargeBufferTest {
#if WINDOWS || XBOX
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main(string[] args)
        {
            using (LargeBufferTestGame game = new LargeBufferTestGame())
            {
                game.Run();
            }
        }
    }
#endif
}

