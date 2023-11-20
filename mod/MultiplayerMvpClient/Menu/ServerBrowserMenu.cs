using System.Runtime.InteropServices;
using Menu;
using Menu.Remix;
using Menu.Remix.MixedUI;
using RWCustom;
using UnityEngine;
using VanillaMenu = Menu.Menu;

namespace MultiplayerMvpClient.Menu
{
	// The menu used to find and connect to servers. Accessed from the main menu.
	// Can directly connect to a specified IP/domain + port, and in future will have a steam-integrated server browser
	public unsafe class ServerBrowserMenu : VanillaMenu
	{
		public const string BACK_BUTTON_SIGNAL = "BACK";
		public const string CONNECT_BUTTON_SIGNAL = "CONNECT";
		public const string DISCONNECT_BUTTON_SIGNAL = "DISCONNECT";
		public const string MAIN_MENU_BUTTON_SIGNAL = "MULTIPLAYER";

		public static readonly ProcessManager.ProcessID ServerBrowserMenuId = new("ServerBrowserMenu", true);

		private OpTextBox ServerIpAddress;

		private OpUpdown ServerPort;

		private HoldButton ConnectButton;

		private AppContainer* appHandle = null;
		private ConnectionTask* connectionTaskHandle = null;

		private bool exiting;

		private bool hasDoneInitUpdate;

		public override bool FreezeMenuFunctions => hasDoneInitUpdate && base.FreezeMenuFunctions;

		public ServerBrowserMenu(ProcessManager manager, ProcessManager.ProcessID ID) : base(manager, ID)
		{
			Vector2 screenSize = manager.rainWorld.screenSize;
			Vector2 screenCenter = manager.rainWorld.screenSize / 2;

			Vector2 bottomLeft = Vector2.zero;
			Vector2 topRight = screenSize;

			Vector2 standardButtonSize = new(110f, 30f);

			Page page = new(this, null, "main", 0);
			pages.Add(page);

			// Background art
			scene = new InteractiveMenuScene(this, page, ModManager.MMF ? manager.rainWorld.options.subBackground : MenuScene.SceneID.Landscape_SU);
			page.subObjects.Add(scene);

			// Fullscreen translucent tint to dim out the background art
			FSprite backgroundTint = new("pixel")
			{
				color = Color.black,
				anchorX = 0,
				anchorY = 0,
				scaleX = screenSize.x + 2,
				scaleY = screenSize.y + 2,
				x = -1,
				y = -1,
				alpha = 0.85f
			};
			page.Container.AddChild(backgroundTint);

			// Fancy title text
			MenuIllustration title = new(this, scene, "", "MultiplayerTitle", Vector2.zero, crispPixels: true, anchorCenter: false);
			title.sprite.shader = manager.rainWorld.Shaders["MenuText"];
			scene.AddIllustration(title);

			// Return to main menu button
			SimpleButton backButton = new(this, page, Translate(BACK_BUTTON_SIGNAL), BACK_BUTTON_SIGNAL, new Vector2(200f, 50f), standardButtonSize);
			page.subObjects.Add(backButton);
			backObject = backButton;

			// Connect to server button
			ConnectButton = new(this, page, Translate(CONNECT_BUTTON_SIGNAL), CONNECT_BUTTON_SIGNAL, new(screenCenter.x, screenSize.y * 0.3f), 30);
			ConnectButton.GetButtonBehavior.greyedOut = true;
			page.subObjects.Add(ConnectButton);

			// SimpleButton disconnectButton = new(this, page, Translate(DISCONNECT_BUTTON_SIGNAL), DISCONNECT_BUTTON_SIGNAL, new Vector2(500, 280), new Vector2(110, 30));
			// page.subObjects.Add(disconnectButton);

			// IP address and port number, layed out to be centered
			{
				Vector2 serverSocketAddressAnchor = new(screenCenter.x, screenSize.y * 0.6f);
				MenuTabWrapper tabWrapper = new(this, page);
				page.subObjects.Add(tabWrapper);

				OpLabel serverIpAddressLabel = new(
					serverSocketAddressAnchor,
					new(20, 24),
					Translate("Server Address:"),
					FLabelAlignment.Left);

				float serverIpAddressLabelWidth = serverIpAddressLabel.label.textRect.width;

				const float serverIpAddressWidth = 274;
				ServerIpAddress = new(
					new Configurable<string>(""),
					serverSocketAddressAnchor,
					 serverIpAddressWidth);

				// Disable connect button when the IP address textbox is empty
				ServerIpAddress.OnValueUpdate += (_, value, _) => ConnectButton.GetButtonBehavior.greyedOut = string.IsNullOrEmpty(value);

				OpLabel serverPortLabel = new(
					serverSocketAddressAnchor,
					 new(20, 24),
					  Translate("Port:"),
					  FLabelAlignment.Left);

				float serverPortLabelWidth = serverPortLabel.label.textRect.width;

				const int upDownOffset = -3;
				const float serverPortWidth = 75;
				ServerPort = new(
					new Configurable<int>(Interop.DEFAULT_PORT, new ConfigAcceptableRange<int>(0, 9999)),
					serverSocketAddressAnchor,
					serverPortWidth);
				ServerPort.PosY += upDownOffset;

				const float padding = 10;
				float totalWidth = serverIpAddressLabelWidth + padding + serverIpAddressWidth + padding + serverPortLabelWidth + padding + serverPortWidth;

				// Lay out the elements relative to eachother so they're centered
				serverIpAddressLabel.PosX = serverSocketAddressAnchor.x - (totalWidth / 2);
				ServerIpAddress.PosX = serverIpAddressLabel.PosX + serverIpAddressLabelWidth + padding;
				serverPortLabel.PosX = ServerIpAddress.PosX + serverIpAddressWidth + padding;
				ServerPort.PosX = serverPortLabel.PosX + serverPortLabelWidth + padding;

				// Actually add the elements to the UI
				_ = new UIelementWrapper(tabWrapper, serverIpAddressLabel);
				_ = new UIelementWrapper(tabWrapper, ServerIpAddress);
				_ = new UIelementWrapper(tabWrapper, serverPortLabel);
				_ = new UIelementWrapper(tabWrapper, ServerPort);
			}

			// Fade out Sundown from main menu
			manager.musicPlayer?.FadeOutAllSongs(25f);
		}

		private string FormatNativeError(Error* error)
		{
			ushort* errorPointer = Interop.format_error(error);
			string errorMessage = Marshal.PtrToStringUni(new(errorPointer));
			Interop.drop_string(errorPointer);
			Interop.drop_error(error);
			return errorMessage;
		}

		private void DisplayNativeError(string text)
		{
			Plugin.Logger.LogInfo($"Native code error: {text}");

			PlaySound(SoundID.MENU_Security_Button_Release);

			// Dialogs automatically disable most UI elements when shown, except for OpTextBox and OpUpDown specifically,
			// which need to be manually disabled and then reenabled
			ServerIpAddress.Unassign();
			ServerPort.Unassign();
			DialogNotify dialog = new(text, manager, () =>
			{
				PlaySound(SoundID.MENU_Button_Standard_Button_Pressed);
				ServerIpAddress.Assign();
				ServerPort.Assign();
			});
			manager.ShowDialog(dialog);
		}

		internal static void SetupHooks()
		{
			On.Menu.MainMenu.ctor += AddMainMenuButton;
			On.ProcessManager.PostSwitchMainProcess += SwitchMainProcess;
		}

		private static void AddMainMenuButton(On.Menu.MainMenu.orig_ctor orig, MainMenu self, ProcessManager manager, bool showRegionSpecificBkg)
		{
			orig(self, manager, showRegionSpecificBkg);

			float buttonWidth = MainMenu.GetButtonWidth(self.CurrLang);
			Vector2 pos = new(683f - (buttonWidth / 2f), 0f);
			Vector2 size = new(buttonWidth, 30f);
			int indexFromBottomOfList = 5;
			self.AddMainMenuButton(new SimpleButton(self, self.pages[0], self.Translate(MAIN_MENU_BUTTON_SIGNAL), MAIN_MENU_BUTTON_SIGNAL, pos, size), () => MainMenuButtonPressed(self), indexFromBottomOfList);
		}

		private static void MainMenuButtonPressed(VanillaMenu from)
		{
			from.manager.RequestMainProcessSwitch(ServerBrowserMenuId);
			from.PlaySound(SoundID.MENU_Switch_Page_In);
		}

		private static void SwitchMainProcess(On.ProcessManager.orig_PostSwitchMainProcess orig, ProcessManager self, ProcessManager.ProcessID ID)
		{
			if (ID == ServerBrowserMenuId)
			{
				self.currentMainLoop = new ServerBrowserMenu(self, ID);
			}
			orig(self, ID);
		}

		public override void Init()
		{
			base.Init();
			init = true;
			Update();
			hasDoneInitUpdate = true;
		}

		public override void RawUpdate(float dt)
		{
			if (appHandle != null)
			{
				bool exitRequested = Convert.ToBoolean(Interop.update_app(appHandle));
				if (exitRequested)
				{
					Plugin.Logger.LogInfo("Native app requested exit");
					Interop.drop_app(appHandle);
					appHandle = null;
					if (connectionTaskHandle != null)
					{
						Interop.drop_connection_task(connectionTaskHandle);
						connectionTaskHandle = null;
					}
				}
			}

			if (connectionTaskHandle != null)
			{
				PollConnectionTaskResult pollResult;
				pollResult = Interop.poll_connection_task(connectionTaskHandle);
				switch (pollResult.tag)
				{
					case PollConnectionTaskResult.Tag.Ok:
						Plugin.Logger.LogInfo("Server connection completed successfully");
						Interop.drop_connection_task(connectionTaskHandle);
						connectionTaskHandle = null;
						break;
					case PollConnectionTaskResult.Tag.Err:
						Interop.drop_connection_task(connectionTaskHandle);
						connectionTaskHandle = null;
						DisplayNativeError(FormatNativeError(pollResult.err._0));
						break;
				}
			}

			base.RawUpdate(dt);
		}

		public override void Update()
		{
			base.Update();

			// Play Bio Engineering on loop
			if (manager.musicPlayer?.song == null)
			{
				manager.musicPlayer?.MenuRequestsSong("RW_43 - Bio Engineering", 1f, 1f);
			}

			// Exit to main menu when Esc is pressed
			if (RWInput.CheckPauseButton(0, manager.rainWorld))
			{
				ExitToMainMenu();
			}
		}

		private void Connect()
		{
			string address = ServerIpAddress.value;
			ushort port = (ushort)ServerPort.valueInt;
			Plugin.Logger.LogInfo($"Connecting to: {address} on port: {port}");

			if (appHandle != null)
			{
				Interop.drop_app(appHandle);
			}
			appHandle = Interop.new_app();

			IntPtr addressPointer = Marshal.StringToHGlobalUni(address);
			AppConnectToServerResult result = Interop.app_connect_to_server(appHandle, (ushort*)addressPointer, port);
			Marshal.FreeHGlobal(addressPointer);

			switch (result.tag)
			{
				case AppConnectToServerResult.Tag.Ok:
					connectionTaskHandle = result.ok._0;
					break;
				case AppConnectToServerResult.Tag.Err:
					DisplayNativeError(FormatNativeError(result.err._0));
					break;
			}
		}

		private void Disconnect()
		{
			if (appHandle != null)
			{
				Interop.drop_app(appHandle);
				appHandle = null;
			}

			if (connectionTaskHandle != null)
			{
				Interop.drop_connection_task(connectionTaskHandle);
				connectionTaskHandle = null;
			}
		}

		private void ExitToMainMenu()
		{
			// Exiting out while a Dialog is up softlocks the game, so don't do that
			if (!exiting && manager.dialog == null)
			{
				exiting = true;
				Disconnect();
				PlaySound(SoundID.MENU_Switch_Page_Out);
				manager.musicPlayer?.FadeOutAllSongs(100f);
				manager.RequestMainProcessSwitch(ProcessManager.ProcessID.MainMenu);
			}
		}

		public override void Singal(MenuObject sender, string message)
		{
			switch (message)
			{
				case CONNECT_BUTTON_SIGNAL:
					Connect();
					break;
				case DISCONNECT_BUTTON_SIGNAL:
					PlaySound(SoundID.MENU_Button_Standard_Button_Pressed);
					Disconnect();
					break;
				case BACK_BUTTON_SIGNAL:
					ExitToMainMenu();
					break;
			}
		}
	}
}