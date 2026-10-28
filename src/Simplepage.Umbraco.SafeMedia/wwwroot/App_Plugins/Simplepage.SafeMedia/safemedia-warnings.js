// Shows SafeMedia warnings for newly created media items as back office notifications.
//
// Why this exists: on Umbraco 17.7 and earlier, the back office rebuilds the response to a request
// that creates an item and keeps only x- headers, so the standard Umb-Notifications header - and
// with it any warning - is dropped on every drag-and-drop upload. Umbraco fixed that in 17.8. For
// a new item SafeMedia sends its warning in X-SafeMedia-Warning instead, which survives the
// rebuild, and this shows it. Updates use the standard route, so nothing is shown twice.
//
// Plain JavaScript with no build step: it only uses modules the back office already provides.
import { umbHttpClient } from '@umbraco-cms/backoffice/http-client';
import { UMB_NOTIFICATION_CONTEXT } from '@umbraco-cms/backoffice/notification';

const HEADER = 'x-safemedia-warning';
let interceptorId;

export const onInit = (host) => {
	interceptorId = umbHttpClient.interceptors.response.use(async (response) => {
		const raw = response.headers.get(HEADER);
		if (!raw) return response;

		try {
			const warnings = JSON.parse(decodeURIComponent(raw));
			const notifications = await host.getContext(UMB_NOTIFICATION_CONTEXT);

			for (const warning of Array.isArray(warnings) ? warnings : []) {
				notifications?.peek('warning', {
					data: { headline: String(warning.title ?? ''), message: String(warning.message ?? '') },
				});
			}
		} catch {
			// A malformed header is ignored: the file has already been checked and saved.
		}

		return response;
	});
};

export const onUnload = () => {
	if (interceptorId !== undefined) {
		umbHttpClient.interceptors.response.eject(interceptorId);
	}
};
