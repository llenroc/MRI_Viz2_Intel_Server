using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using GroupLab.iNetwork;
using GroupLab.iNetwork.Tcp;

namespace MRIViz_Intel
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Class Instance Variables
        private Server _server;
        private List<Connection> _clients;
        #endregion


        /////////////////////
        //constants//
        const short DistanceFromScreenEdge = 350; //34.95cm, 13.76"
        const short ScreenWidth = 318; //31.75cm, 12.5"
        const short ScreenLenght = 889; //88.9cm, 35"
        const short FOV = 72; //actually 73, rounded down to accomodate error
        /////////////////////


        #region Constructors
        public unsafe MainWindow()
        {
            InitializeComponent();
            InitializeServer();

            this.Closing += new CancelEventHandler(OnWindowClosing);

            System.Windows.Threading.Dispatcher uiDispatcher = this.Dispatcher;

            UtilMPipeline pp = new UtilMPipeline();

            BackgroundWorker worker = new BackgroundWorker();

            worker.DoWork += delegate(object s, DoWorkEventArgs args)
            {
                pp.EnableImage(PXCMImage.ColorFormat.COLOR_FORMAT_DEPTH);
                pp.Init();



                for (; ; )
                {
                    if (!pp.AcquireFrame(true)) break;
                    {
                        PXCMImage image = pp.QueryImage(PXCMImage.ImageType.IMAGE_TYPE_DEPTH);

                        PXCMImage.ImageData ddata;

                        pxcmStatus sts = image.AcquireAccess(PXCMImage.Access.ACCESS_READ, out ddata);
                        if (sts < pxcmStatus.PXCM_STATUS_NO_ERROR) break;
                        short* depth = (short*)ddata.buffer.planes[0];

                        short minZ = 32001;
                        int minX = 0;
                        int minY = 0;
                        short maxX = 320;

                        for (int y = 0; y < 240; y++)
                        {

                            for (int x = 0; x < 320; x++)
                            {
                                if (depth[(y * 320) + x] < minZ)
                                {
                                    minZ = depth[(y * 320) + x];
                                    minX = x;
                                    minY = y;
                                }

                            }
                        }

                        /////////////////////
                        //normalize extremes//
                        if (minZ < DistanceFromScreenEdge) minZ = DistanceFromScreenEdge;
                        //match origin//
                        minX -= 160;
                        minY -= 120;
                        /////////////////////
                        maxX = (short)(Math.Tan(FOV / 2) * minZ);
                        short discardedX = (short)(Math.Tan(FOV / 2) * (minZ - DistanceFromScreenEdge));
                        short screenMaxX = (short)(maxX - discardedX);
                        float xRatio = minX / screenMaxX;

                        UpdateGridDelegate update = new UpdateGridDelegate(UpdateGrid);
                        uiDispatcher.BeginInvoke(update, minX, minY, minZ, maxX, discardedX, screenMaxX, xRatio);

                        image.ReleaseAccess(ref ddata);
                        pp.ReleaseFrame();
                    }
                }

                pp.Close();
                pp.Dispose();
            };

            worker.RunWorkerAsync();

        
        }
        #endregion


        #region Initialization
        private void InitializeServer()
        {
            this._clients = new List<Connection>();

            // Creates a new server with the given name and is bound to the given port.
            this._server = new Server("Creative", 12345);
            this._server.IsDiscoverable = true;
            this._server.Connection += new ConnectionEventHandler(OnServerConnection);
            this._server.Start();

            this._statusLabel.Content = "IP: " + this._server.Configuration.IPAddress.ToString()
                + ", Port: " + this._server.Configuration.Port.ToString();
        }

        // Handles the event that a connection is made
        private void OnServerConnection(object sender, ConnectionEventArgs e)
        {
            if (e.ConnectionEvent == ConnectionEvents.Connect)
            {
                // Lock the list so that only the networking thread affects it
                lock (this._clients)
                {
                    if (!(this._clients.Contains(e.Connection)))
                    {
                        // Add to list and create event listener
                        this._clients.Add(e.Connection);
                        e.Connection.MessageReceived += new ConnectionMessageEventHandler(OnConnectionMessage);

                        // Using the GUI thread make changes
                        this.Dispatcher.Invoke(
                            new Action(
                                delegate()
                                {
                                    this._clientsList.Items.Add(e.Connection);
                                }
                        ));
                    }
                }
            }
            else if (e.ConnectionEvent == ConnectionEvents.Disconnect)
            {
                // Lock the list so that only the networking thread affects it
                lock (this._clients)
                {
                    if (this._clients.Contains(e.Connection))
                    {
                        // Clean up --  remove from list and remove event listener
                        this._clients.Remove(e.Connection);
                        e.Connection.MessageReceived -= new ConnectionMessageEventHandler(OnConnectionMessage);

                        // Using the GUI thread make changes
                        this.Dispatcher.Invoke(
                            new Action(
                                delegate()
                                {
                                    this._clientsList.Items.Remove(e.Connection);
                                }
                        ));
                    }
                }
            }
        }
        #endregion

        #region intel-related functions

        public unsafe void UpdateGrid(int x, int y, int z, short maxX, short discardedX, short screenMaxX, float xRatio)
        {
            //Grid.SetColumn(marker, (int)(960 / xy.X));
            //Grid.SetRow(marker, 11-(int)(720 / xy.Y*3));
            this.coordinates0.Content = ("Raw X is " + string.Format("{0:N}", x.ToString()));
            this.coordinates1.Content = ("Raw Y is " + string.Format("{0:N}", y.ToString()));
            this.coordinates2.Content = ("Raw Z is " + string.Format("{0:N}", z.ToString()));
            this.coordinates3.Content = ("Maximum X is " + string.Format("{0:N}", maxX.ToString()));
            this.coordinates4.Content = ("Discarded X is " + string.Format("{0:N}", discardedX.ToString()));
            // not really useful
            this.coordinates5.Content = ("Screen's Max X is " + string.Format("{0:N}", screenMaxX.ToString()));
            //
            this.coordinates6.Content = ("The X ratio is " + string.Format("{0:N}", xRatio.ToString()));

            //
            // create and send location to the clients
            //
            Message newMessage = new Message("getImage");
            int zIndexValue = z - DistanceFromScreenEdge;
            newMessage.AddField("z", zIndexValue);
            this._server.BroadcastMessage(newMessage);

            setImageImageOnDisplay(zIndexValue, 0, 0);
        }

        public delegate void UpdateGridDelegate(int x, int y, int z, short maxX, short discardedX, short screenMaxX, float xRatio);


        #endregion

        #region Main Body
        // Handles the event that a message is received
        private void OnConnectionMessage(object sender, Message msg)
        {
            if (msg != null)
            {
                // Check message name...
                switch (msg.Name)
                {
                    default:
                        //Do nothing
                        break;
                }
            }
        }


        // When server window closes, stop running the server
        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (this._server != null && this._server.IsRunning)
            {
                this._server.Stop();
            }
        }
        #endregion


        private void sendImageIndex(int slValue, int x, int y)
        {
            // TODO MESSAGE
            Message msg = new Message("ChangeImg");
            msg.AddField("index", slValue);
            msg.AddField("x", x);
            msg.AddField("y", y);
            if (this._server != null)
                this._server.BroadcastMessage(msg);
            Console.WriteLine(msg.ToString() + ": " + msg.GetIntField("index"));
        }

        int processCoordinates(double x, double y, double z)
        {
            // Calculate the width of column in space [width in space divided by number of images]

            // Calculate the index of image = [position in space divided by calculated column's width]

            return 541;
        }

        void setImageImageOnDisplay(int imageIndex, int x, int y)
        {
            imageIndex += 541;

            String imgUri = "MRIImages/IM-0001-0" + imageIndex + ".jpg";

            this.Dispatcher.Invoke(new Action(delegate()
            {
                image.Source = new BitmapImage(new Uri(imgUri, UriKind.Relative));
                // image.SetValue(Canvas.LeftProperty, (double)x);

                // image.SetValue(Canvas.TopProperty, (double)y);
            }));
        }
    }
}
