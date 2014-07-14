﻿define(
	['app',
		'common',
		'webapp/scripts/script-list-model',
		'webapp/scripts/script-list-view'],
	function (application, commonModule) {

		application.module('Scripts.List', function (module, app, backbone, marionette, $, _) {

			var api = {

				runScript: function (view) {

					var scriptId = view.model.get('id');

					app.request('update:scripts:run', scriptId).done(function () {

						var name = view.model.get('name');
						commonModule.utils.alert('The script "{0}" has been executed.', name);
					});
				},

				deleteScript: function (view) {

					var scriptName = view.model.get('name');

					if (commonModule.utils.confirm('Delete the script "{0}"?', scriptName)) {

						var scriptId = view.model.get('id');
						app.request('update:scripts:delete', scriptId).done(api.reload);
					}
				},

				addScriptTile: function (view) {

					var scriptId = view.model.get('id');
					app.addTile('ThinkingHome.Plugins.Scripts.ScriptsTileDefinition', { id: scriptId });
				},

				addScript: function () {

					app.trigger('page:load', 'webapp/scripts/script-editor');
				},
				editScript: function (itemView) {

					var scriptId = itemView.model.get('id');
					app.trigger('page:load', 'webapp/scripts/script-editor', scriptId);
				},
				reload: function () {

					var rq = app.request('load:scripts:list');

					$.when(rq).done(function (items) {

						var view = new module.ScriptListView({ collection: items });

						view.on('scripts:add', api.addScript);
						view.on('itemview:scripts:edit', api.editScript);
						view.on('itemview:scripts:run', api.runScript);
						view.on('itemview:scripts:delete', api.deleteScript);
						view.on('itemview:scripts:add-tile', api.addScriptTile);

						app.setContentView(view);
					});
				}
			};

			module.start = function () {
				api.reload();
			};

		});

		return application.Scripts.List;
	});