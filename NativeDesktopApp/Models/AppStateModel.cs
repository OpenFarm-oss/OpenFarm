using DatabaseAccess;
using RabbitMQHelper;

namespace NativeDesktopApp.Models;

public class AppStateModel
{
    private readonly DatabaseAccessHelper _databaseAccessHelper;
    private readonly IRmqHelper _rmqHelper;

    public AppStateModel(DatabaseAccessHelper databaseAccessHelper, IRmqHelper rmqHelper)
    {
        _rmqHelper = rmqHelper;
        _databaseAccessHelper = databaseAccessHelper;
    }

}