using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace MJPEGStreamer
{
    public static class Helpers
    {
        public static string streamPassword = "";

        public static bool CheckInternetConnection()
        {
            try
            {
                bool isNetworkConnected = NetworkInterface.GetIsNetworkAvailable();
                if (isNetworkConnected)
                {
                    //Check WIFI
                    ConnectionProfile InternetConnectionProfile = NetworkInformation.GetInternetConnectionProfile();
                    bool isWLANConnection = (InternetConnectionProfile == null) ? false : InternetConnectionProfile.IsWlanConnectionProfile;
                    if (isWLANConnection)
                    {
                        return true;
                    }
                    else
                    {
                        ConnectionProfile InternetConnectionMobile = NetworkInformation.GetInternetConnectionProfile();
                        bool isMobileConnection = (InternetConnectionMobile == null) ? false : InternetConnectionMobile.IsWwanConnectionProfile;
                        if (isMobileConnection)
                        {
                            return true;
                        }
                        else
                        {
                            //LAN
                            ConnectionProfile LANConnectionProfile = NetworkInformation.GetInternetConnectionProfile();
                            if (LANConnectionProfile != null)
                            {
                                return true;
                            }
                            else
                            {
                                ConnectionProfile connections = NetworkInformation.GetInternetConnectionProfile();
                                bool internet = connections != null && connections.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
                                if (internet)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {

            }
            return false;
        }
    }
    
}
