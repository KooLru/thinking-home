﻿using System;
using System.ComponentModel.Composition;
using System.Configuration;
using System.Linq;
using System.Text;
using ECM7.Migrator.Framework;
using NHibernate;
using NHibernate.Linq;
using NHibernate.Mapping.ByCode;
using ThinkingHome.Core.Plugins;
using ThinkingHome.Plugins.Mqtt.Model;
using ThinkingHome.Plugins.Scripts;
using ThinkingHome.Plugins.Timer.Attributes;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using ThinkingHome.Plugins.Listener;

[assembly: MigrationAssembly("ThinkingHome.Plugins.Mqtt")]

namespace ThinkingHome.Plugins.Mqtt
{
	[Plugin]
	public class MqttPlugin : PluginBase
	{
		#region settings

		private const int DEFAULT_PORT = 1883;

		public static string Host
		{
			get { return ConfigurationManager.AppSettings["Mqtt.Host"]; }
		}

		public static int Port
		{
			get
			{
				int port;
				return int.TryParse(ConfigurationManager.AppSettings["Mqtt.Port"], out port)
					? port
					: DEFAULT_PORT;
			}
		}

		public static string Login
		{
			get { return ConfigurationManager.AppSettings["Mqtt.Login"]; }
		}

		public static string Password
		{
			get { return ConfigurationManager.AppSettings["Mqtt.Password"]; }
		}

		public static string Path
		{
			get { return ConfigurationManager.AppSettings["Mqtt.Path"]; }
		}

		#endregion

		#region messages

		private readonly object lockObject = new object();

		private MqttClient client;

		private bool enabled;

		private const string SETTINGS_MESSAGE_FORMAT = "{0} is required but it is not specified - check the \"{1}\" parameter";

		public override void InitDbModel(ModelMapper mapper)
		{
			mapper.Class<ReceivedData>(cfg => cfg.Table("Mqtt_ReceivedData"));
		}

		public override void InitPlugin()
		{
			//Debugger.Launch();

			bool isValidSettings = true;

			if (string.IsNullOrWhiteSpace(Host))
			{
				Logger.Warn(SETTINGS_MESSAGE_FORMAT, "mqtt host", "Mqtt.Host");
				isValidSettings = false;
			}

			if (string.IsNullOrWhiteSpace(Path))
			{
				Logger.Warn(SETTINGS_MESSAGE_FORMAT, "mqtt subscription path", "Mqtt.Path");
				isValidSettings = false;
			}

			if (isValidSettings)
			{
				Logger.Info("init mqtt client: {0}:{1}", Host, Port);
				client = new MqttClient(Host, Port, false, null);

				client.ConnectionClosed += client_ConnectionClosed;
				client.MqttMsgPublishReceived += client_MqttMsgPublishReceived;
			}
		}

		void client_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
		{
			Logger.Info("mqtt client {0}: message received\ntopic: {1}, message: {2}", ((MqttClient)sender).ClientId, e.Topic, Encoding.UTF8.GetString(e.Message));

			using (var session = Context.OpenSession())
			{
				var entity = session.Query<ReceivedData>().FirstOrDefault(x => x.Path == e.Topic)
					?? new ReceivedData
						{
							Id = Guid.NewGuid(),
							Path = e.Topic
						};

				entity.Timestamp = DateTime.Now;
				entity.Message = Convert.ToBase64String(e.Message);

				session.Save(entity);
				session.Flush();

				// events
				var message = CreateMqttMessage(entity);
				Run(OnMessageReceivedForPlugins, x => x(message));

				this.RaiseScriptEvent(x => x.OnMessageReceivedForScripts, message.path);

				Context.GetPlugin<ListenerPlugin>().Send("mqtt:message", message);
            }
		}

		void client_ConnectionClosed(object sender, EventArgs e)
		{
			Logger.Info("mqtt client {0}: connection closed", ((MqttClient)sender).ClientId);
		}

		public override void StartPlugin()
		{
			enabled = true;

			ReConnect();
		}

		[RunPeriodically(1)]
		private void ReConnect()
		{
			if (client != null && !client.IsConnected && enabled)
			{
				lock (lockObject)
				{
					if (client != null && !client.IsConnected && enabled)
					{
						try
						{
							var clientId = Guid.NewGuid().ToString();

							Logger.Info("connect to mqtt broker using clientId: {0}", clientId);

							if (string.IsNullOrWhiteSpace(Login) || string.IsNullOrWhiteSpace(Password))
							{
								client.Connect(clientId);
							}
							else
							{
								client.Connect(clientId, Login, Password);
							}

							Logger.Info("subscribe to channel: {0}", Path);
							client.Subscribe(new[] { Path }, new[] { MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE });

						}
						catch (Exception ex)
						{
							Logger.WarnException("mqtt: connection failed", ex);
						}
					}
				}
			}
		}

		public override void StopPlugin()
		{
			Logger.Info("disconnect mqtt client: id {0}", client.ClientId);

			if (client.IsConnected)
			{
				client.Disconnect();
			}

			base.StopPlugin();
		}

		#endregion

		#region events

		[ScriptEvent("mqtt.messageReceived")]
		public ScriptEventHandlerDelegate[] OnMessageReceivedForScripts { get; set; }

		[ImportMany("25DD679C-BB4E-449A-BFB8-42CC877CC32C")]
		public Action<MqttMessage>[] OnMessageReceivedForPlugins { get; set; }

		#endregion

		#region api: read

		[ScriptCommand("mqttReadMessage")]
		public MqttMessage ScriptReadMessage(string path)
		{
			return Read(path);
		}

		public MqttMessage Read(string path, ISession session = null)
		{
			if (session == null)
			{
				using (session = Context.OpenSession())
				{
					return Read(path, session);
				}
			}

			var message = session
				.Query<ReceivedData>()
				.FirstOrDefault(m => m.Path == path);

			return message == null
				? null
				: CreateMqttMessage(message);
		}

		private MqttMessage CreateMqttMessage(ReceivedData message)
		{
			byte[] data;

			try
			{
				data = Convert.FromBase64String(message.Message);
			}
			catch (Exception ex)
			{
				data = null;
				Logger.WarnException("incorrect MQTT message", ex);
			}

			return new MqttMessage
			{
				path = message.Path,
				timestamp = message.Timestamp,
				message = data
			};
		}

		#endregion

		#region api: send

		[ScriptCommand("mqttSendMessage")]
		public bool ScriptSendMessage(string path, string message)
		{
			return Send(path, message);
		}

		public bool Send(string path, string message)
		{
			return Send(path, Encoding.UTF8.GetBytes(message));
		}

		public bool Send(string path, byte[] message)
		{
			Logger.Info("send MQTT message: path={0}", path);

			if (client != null && client.IsConnected)
			{
				lock (lockObject)
				{
					if (client != null && client.IsConnected)
					{
						try
						{
							client.Publish(path, message ?? new byte[0]);
							return true;
						}
						catch (Exception ex)
						{
							Logger.ErrorException("MQTT publishing failed", ex);
							return false;
						}
					}
				}
			}

			Logger.Warn("MQTT client is disconnected");
			return false;
		}

		#endregion
	}
}
